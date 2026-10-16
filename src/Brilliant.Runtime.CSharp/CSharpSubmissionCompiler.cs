using System.Collections.Immutable;
using System.Text;
using Brilliant.Code.CSharp;
using Brilliant.Core.Code;
using Brilliant.Core.Content;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Brilliant.Runtime.CSharp;

/// <summary>Pinned synchronous compiler shared by native submissions and the isolated CLI publishing worker.</summary>
public static class CSharpSubmissionCompiler
{
    public sealed record Compilation(byte[]? Assembly, TestRunResult? Error);
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        using var resource = typeof(CSharpSubmissionCompiler).Assembly.GetManifestResourceStream("probe-runtime.dll")
            ?? throw new InvalidOperationException("The packaged probe metadata is missing.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return Basic.Reference.Assemblies.Net100.References.All.Cast<MetadataReference>()
            .Append(MetadataReference.CreateFromImage(buffer.ToArray())).ToImmutableArray();
    });

    public static Compilation Compile(string code, string entrypoint, IReadOnlyList<CodeTest> tests, bool captureConsole = false, CancellationToken cancellationToken = default) =>
        Compile(code, entrypoint, tests, captureConsole, allowBlockingCalls: false, cancellationToken);

    internal static Compilation Compile(string code, string entrypoint, IReadOnlyList<CodeTest> tests, bool captureConsole, bool allowBlockingCalls, CancellationToken cancellationToken)
    {
        if (code.Length > 65_536 || tests.Count is < 1 or > 64 || tests.Any(t => t.Input.Length > 4_096 || t.Expected.Length > 4_096))
            return Error("C# exercises support up to 64 tests, 64 KiB of source and 4 KiB per test expression.");
        cancellationToken.ThrowIfCancellationRequested();
        if (tests.Any(t => t.Compare is not null)) return Error("C# tests use structural equality; Python compare options are unsupported.");
        if (tests.Any(t => t.MaxAllocatedBytes is < 0)) return Error("An allocation budget can't be negative.");
        if (!SyntaxFacts.IsValidIdentifier(entrypoint)) return Error("C# entrypoint must be an unqualified function identifier.");
        if (!LanguageVersionFacts.TryParse(CSharpPins.LanguageVersion, out var languageVersion)) return Error("Unsupported pinned C# language version.");
        var options = new CSharpParseOptions(languageVersion);
        var source = CSharpSyntaxTree.ParseText(code, options, "solution.cs", Encoding.UTF8, cancellationToken);
        var root = source.GetCompilationUnitRoot();
        if (root.DescendantTrivia(descendIntoTrivia: true).Any(t => t.IsKind(SyntaxKind.DefineDirectiveTrivia) || t.IsKind(SyntaxKind.UndefDirectiveTrivia)))
            return Error("Source-local #define/#undef directives are not supported by the C# reference gate.");
        // These require lifecycle and thread propagation rules beyond the synchronous publishing gate.
        if (root.DescendantNodes().Any(n => n is AwaitExpressionSyntax or YieldStatementSyntax or UnsafeStatementSyntax)
            || root.DescendantTokens().Any(t => t.IsKind(SyntaxKind.AsyncKeyword) || t.IsKind(SyntaxKind.UnsafeKeyword)))
            return Error("The C# reference gate currently supports synchronous, safe code; async/iterator/unsafe execution is not supported.");
        if (root.DescendantNodes().OfType<EqualsValueClauseSyntax>().Any(n =>
                n.Parent is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax }
                    or PropertyDeclarationSyntax
                && n.Value.DescendantNodesAndSelf().Any(v => v is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)))
            return Error("Calling code from field/property initializers is not supported by the C# budget gate; move it into a method.");

        var text = new StringBuilder("using System;\nusing System.Collections.Generic;\nusing System.Linq;\n");
        foreach (var directive in root.Usings) text.AppendLine(directive.ToFullString());
        text.AppendLine("public static class __BrilliantReference { public static object?[] Run(int __test, int __measure) {");
        foreach (var member in root.Members.OfType<GlobalStatementSyntax>()) AppendSource(text, member);
        text.AppendLine("switch (__test) {");
        for (var i = 0; i < tests.Count; i++)
        {
            var test = tests[i];
            text.AppendLine($"#line 1 \"test-{i + 1}.cs\"");
            // Mode 1 makes one bare call so the runtime can count the bytes a call allocates; the result is discarded, not boxed.
            var measure = test.MaxAllocatedBytes is null ? "" : $"if (__measure != 0) {{ _ = {entrypoint}({test.Input}); return System.Array.Empty<object?>(); }} ";
            text.AppendLine($"case {i}: {measure}return new object?[] {{ (object?)({entrypoint}({test.Input})), (object?)({test.Expected}) }};");
        }
        text.AppendLine("#line default\ndefault: throw new System.ArgumentOutOfRangeException(); } } }");
        foreach (var member in root.Members.Where(m => m is not GlobalStatementSyntax)) AppendSource(text, member);
        var tree = CSharpSyntaxTree.ParseText(text.ToString(), options, "reference.cs", Encoding.UTF8, cancellationToken);
        var references = References.Value;
        var compilation = CSharpCompilation.Create("BrilliantReference_" + Guid.NewGuid().ToString("N"), [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: false));
        var errors = compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0) return CompileError(errors, entrypoint, tests);
        try
        {
            if (captureConsole)
            {
                NativeCodePolicy.Validate(tree, compilation.GetSemanticModel(tree), allowBlockingCalls, cancellationToken);
                var captured = tree.WithRootAndOptions(new ConsoleCaptureRewriter(compilation.GetSemanticModel(tree)).Visit(tree.GetRoot())!, tree.Options);
                compilation = compilation.ReplaceSyntaxTree(tree, captured);
                tree = captured;
            }
            var rewritten = ProbeRewriter.Rewrite(tree, semanticModel: compilation.GetSemanticModel(tree));
            compilation = compilation.ReplaceSyntaxTree(tree, rewritten);
        }
        catch (NotSupportedException ex) { return Error(ex.Message); }
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe, cancellationToken: cancellationToken);
        if (!emit.Success) return CompileError(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), entrypoint, tests);
        if (pe.Length > 1_048_576) return Error("The compiled C# submission exceeds 1 MiB.");
        return new(pe.ToArray(), null);
    }

    private static void AppendSource(StringBuilder text, MemberDeclarationSyntax member)
    {
        text.AppendLine($"#line {member.GetLocation().GetLineSpan().StartLinePosition.Line + 1} \"solution.cs\"");
        text.AppendLine(member.ToString());
        text.AppendLine("#line default");
    }

    private static readonly System.Text.RegularExpressions.Regex TestFile = new(@"^test-(\d+)\.cs$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The editor highlights only lines in the learner's code. An error in a generated test call (for example a signature that
    /// doesn't match the call) has no learner line, so it is explained in terms of the test instead of a harness file.
    /// </summary>
    private static Compilation CompileError(IEnumerable<Diagnostic> errors, string entrypoint, IReadOnlyList<CodeTest> tests)
    {
        var list = errors.ToArray();
        var learner = list.FirstOrDefault(d => d.Location.GetMappedLineSpan().Path == "solution.cs");
        var line = learner?.Location.GetMappedLineSpan().StartLinePosition.Line + 1;
        var message = "C# compilation failed.";
        if (learner is null && list.Select(d => TestFile.Match(d.Location.GetMappedLineSpan().Path)).FirstOrDefault(m => m.Success) is { } match)
        {
            var number = int.Parse(match.Groups[1].Value);
            message = $"Test {number} calls {entrypoint}({tests[number - 1].Input}); check your method's name, parameters and return type.";
        }
        var details = list.Select(d => TestFile.Match(d.Location.GetMappedLineSpan().Path) is { Success: true } m
            ? $"test {m.Groups[1].Value}: error {d.Id}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}"
            : d.ToString());
        return new(null, new(TestRunStatus.Error, "", message, string.Join("\n", details), line, []));
    }
    private static Compilation Error(string message) => new(null, new(TestRunStatus.Error, "", message, null, null, []));
}
