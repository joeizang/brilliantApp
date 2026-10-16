using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Brilliant.Code.CSharp;
using Brilliant.Core.Code;
using Brilliant.Core.Content;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Brilliant.Cli;

/// <summary>CoreCLR publishing gate. A killable child process contains blocking library calls and process-wide effects.</summary>
public sealed class CSharpReferenceSolutionRunner(TimeSpan? timeout = null) : IReferenceSolutionRunner
{
    public string Language => "csharp";
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(CSharpReferenceSolutionRunner).Assembly.Location);
        start.ArgumentList.Add("--csharp-reference-worker");
        try
        {
            using var process = Process.Start(start) ?? throw new ReferenceRunnerUnavailableException("could not start the C# reference worker.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(JsonSerializer.Serialize(new Request(code, entrypoint, tests), Json));
            process.StandardInput.Close();
            if (!process.WaitForExit(_timeout))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return Error($"C# reference worker exceeded {_timeout.TotalSeconds:0.##} seconds.", TestRunStatus.TimedOut);
            }
            var output = stdout.GetAwaiter().GetResult();
            var errors = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0) return Error($"C# reference worker exited with code {process.ExitCode}. {errors}");
            try { return JsonSerializer.Deserialize<TestRunResult>(output, Json) ?? Error("C# reference worker returned no result."); }
            catch (JsonException) { return Error("C# reference worker returned an invalid result."); }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ReferenceRunnerUnavailableException($"could not start the .NET 10 C# reference worker: {ex.Message}");
        }
    }

    public TraceResult Trace(string code, IReadOnlyList<Visual> visuals) =>
        new(TraceStatus.Error, [], "", "C# recorded trace publishing follows in #153; a Python checker cannot validate C#.", null, null);

    public static int RunWorker(TextReader input, TextWriter output)
    {
        TestRunResult result;
        try
        {
            var request = JsonSerializer.Deserialize<Request>(input.ReadToEnd(), Json)
                ?? throw new InvalidOperationException("Missing C# reference request.");
            result = CompileAndRun(request);
        }
        catch (Exception ex) { result = Error(ex.Message); }
        output.Write(JsonSerializer.Serialize(result, Json));
        return 0;
    }

    private sealed record Request(string Code, string Entrypoint, IReadOnlyList<CodeTest> Tests);

    private static TestRunResult CompileAndRun(Request request)
    {
        if (request.Tests.Any(t => t.Compare is not null)) return Error("C# tests use structural equality; Python compare options are unsupported.");
        if (!SyntaxFacts.IsValidIdentifier(request.Entrypoint)) return Error("C# entrypoint must be an unqualified function identifier.");
        LanguageVersionFacts.TryParse(CSharpPins.LanguageVersion, out var languageVersion);
        var options = new CSharpParseOptions(languageVersion);
        var source = CSharpSyntaxTree.ParseText(request.Code, options, "solution.cs");
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
        text.AppendLine("public static class __BrilliantReference { public static object?[] Run(int __test) {");
        foreach (var member in root.Members.OfType<GlobalStatementSyntax>()) AppendSource(text, member);
        text.AppendLine("switch (__test) {");
        for (var i = 0; i < request.Tests.Count; i++)
        {
            var test = request.Tests[i];
            text.AppendLine($"#line 1 \"test-{i + 1}.cs\"");
            text.AppendLine($"case {i}: return new object?[] {{ (object?)({request.Entrypoint}({test.Input})), (object?)({test.Expected}) }};");
        }
        text.AppendLine("#line default\ndefault: throw new System.ArgumentOutOfRangeException(); } } }");
        foreach (var member in root.Members.Where(m => m is not GlobalStatementSyntax)) AppendSource(text, member);
        var tree = CSharpSyntaxTree.ParseText(text.ToString(), options, "reference.cs");
        var references = Basic.Reference.Assemblies.Net100.References.All.Cast<MetadataReference>()
            .Append(MetadataReference.CreateFromFile(typeof(ProbeBudget).Assembly.Location));
        var compilation = CSharpCompilation.Create("BrilliantReference_" + Guid.NewGuid().ToString("N"), [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: false));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0) return CompileError(errors);
        try
        {
            var rewritten = ProbeRewriter.Rewrite(tree, semanticModel: compilation.GetSemanticModel(tree));
            compilation = compilation.ReplaceSyntaxTree(tree, rewritten);
        }
        catch (NotSupportedException ex) { return Error(ex.Message); }
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        if (!emit.Success) return CompileError(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        pe.Position = 0;
        var context = new AssemblyLoadContext("C# reference", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(pe);
            var method = assembly.GetType("__BrilliantReference")!.GetMethod("Run")!;
            var outcomes = new List<TestOutcome>();
            for (var i = 0; i < request.Tests.Count; i++)
            {
                var index = i;
                object?[]? pair = null;
                Exception? failure = null;
                string? actual = null;
                string? expected = null;
                var passed = false;
                using var printed = new StringWriter();
                var previous = Console.Out;
                var previousError = Console.Error;
                var thread = new Thread(() =>
                {
                    var budget = new ProbeBudget();
                    ProbeBudget.Current = budget;
                    try
                    {
                        pair = (object?[])method.Invoke(null, [index])!;
                        budget.Tick();
                        passed = CSharpValues.Equal(pair[0], pair[1]);
                        actual = CSharpValues.Format(pair[0]);
                        expected = CSharpValues.Format(pair[1]);
                    }
                    catch (Exception ex) { failure = Unwrap(ex); passed = false; }
                    finally { ProbeBudget.Current = null; }
                }, ProbeBudget.DefaultStackSize) { IsBackground = true, Name = "C# reference test" };
                Console.SetOut(printed);
                Console.SetError(printed);
                try { thread.Start(); thread.Join(); }
                finally { Console.SetOut(previous); Console.SetError(previousError); }
                if (failure is BudgetExceededException) return Error(failure.Message, TestRunStatus.TimedOut);
                var test = request.Tests[i];
                outcomes.Add(new TestOutcome($"{request.Entrypoint}({test.Input})", expected ?? test.Expected,
                    actual, passed,
                    printed.ToString(), failure?.ToString(), null));
            }
            return new TestRunResult(outcomes.All(t => t.Passed) ? TestRunStatus.Passed : TestRunStatus.Failed, "", null, null, null, outcomes);
        }
        finally { context.Unload(); }
    }

    private static void AppendSource(StringBuilder text, MemberDeclarationSyntax member)
    {
        text.AppendLine($"#line {member.GetLocation().GetLineSpan().StartLinePosition.Line + 1} \"solution.cs\"");
        text.AppendLine(member.ToString());
        text.AppendLine("#line default");
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : ex;
    private static TestRunResult CompileError(IEnumerable<Diagnostic> errors)
    {
        var list = errors.ToArray();
        var line = list.FirstOrDefault()?.Location.GetMappedLineSpan().StartLinePosition.Line + 1;
        return new(TestRunStatus.Error, "", "C# compilation failed.", string.Join("\n", list.Select(d => d.ToString())), line, []);
    }
    private static TestRunResult Error(string message, TestRunStatus status = TestRunStatus.Error) => new(status, "", message, null, null, []);
}
