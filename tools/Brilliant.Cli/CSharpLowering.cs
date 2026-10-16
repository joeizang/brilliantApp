using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Basic.Reference.Assemblies;
using Brilliant.Core.Code;
using Brilliant.Core.Content;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Brilliant.Cli;

/// <summary>
/// Turns a C# snippet into what the compiler makes of it, for the <c>lowered</c> step (#154). Roslyn compiles the snippet in Release mode
/// to an in-memory assembly (the same pinned compiler and reference set the app uses, so what is shown is what that compiler produces);
/// ICSharpCode.Decompiler then reads the assembly back with its reconstructions of <c>async</c>, <c>foreach</c>, <c>using</c> and <c>lock</c>
/// switched off, so the state machines, enumerators and try/finally blocks the compiler wrote stay visible, and prints the IL.
/// Nothing is run, so this needs no worker process. Output is deterministic: same snippet, same pinned compiler, same text.
/// </summary>
internal static class CSharpLowering
{
    /// <summary>Most lines either form may have, so a snippet that lowers to a wall of text is cut down by its author rather than shipped.</summary>
    public const int MaxLines = 1000;
    public const int MaxSourceLength = 16_384;

    private const string ImplicitUsings = "global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\n";

    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() => Net100.References.All.Cast<MetadataReference>().ToList());

    /// <summary>The lowered forms of <paramref name="source"/>, or why it can't be lowered (the message names the line that doesn't compile).</summary>
    public static (LoweredCode? Lowered, string? Error) Lower(string source)
    {
        if (source.Length > MaxSourceLength) return (null, $"the code is {source.Length} characters; a lowered view takes at most {MaxSourceLength}.");
        if (!LanguageVersionFacts.TryParse(CSharpPins.LanguageVersion, out var languageVersion)) return (null, "unsupported pinned C# language version.");

        var options = new CSharpParseOptions(languageVersion);
        var tree = CSharpSyntaxTree.ParseText(source, options, "lowered.cs");
        var usings = CSharpSyntaxTree.ParseText(ImplicitUsings, options, "usings.cs");
        var program = tree.GetCompilationUnitRoot().Members.OfType<GlobalStatementSyntax>().Any();
        var compilation = CSharpCompilation.Create("Lowered", [tree, usings], References.Value,
            new CSharpCompilationOptions(program ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, deterministic: true, allowUnsafe: true,
                // Nullable annotations only add attributes to the output; leaving them off keeps the lowered code about the snippet.
                nullableContextOptions: NullableContextOptions.Disable));

        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success)
        {
            var error = emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                // The snippet's own errors first, in source order; a problem in the implicit usings or elsewhere must not hide the line the author needs.
                .OrderBy(d => d.Location.SourceTree == tree ? 0 : 1).ThenBy(d => d.Location.IsInSource ? d.Location.SourceSpan.Start : 0).First();
            var line = error.Location.IsInSource && error.Location.SourceTree == tree ? $"line {error.Location.GetLineSpan().StartLinePosition.Line + 1}: " : "";
            return (null, $"the code does not compile ({line}{error.Id} {error.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}).");
        }

        image.Position = 0;
        using var file = new ICSharpCode.Decompiler.Metadata.PEFile("Lowered" + (program ? ".exe" : ".dll"), image, PEStreamOptions.PrefetchEntireImage);
        var types = file.Metadata.GetTopLevelTypeDefinitions().Where(t => IsAuthored(file.Metadata, t)).ToList();
        if (types.Count == 0) return (null, "the code declares nothing the compiler lowers (no type, method or top-level statement).");

        var settings = new DecompilerSettings(ICSharpCode.Decompiler.CSharp.LanguageVersion.CSharp1)
        {
            // CSharp1 turns most reconstructions off; these are the ones it leaves on and that would hide the lowering.
            ForEachStatement = false, UsingStatement = false, LockStatement = false, AsyncEnumerator = false,
            ShowXmlDocumentation = false,
            ThrowOnAssemblyResolveErrors = false,
            AutoLoadAssemblyReferences = false,
        };
        var decompiler = new CSharpDecompiler(file, new UniversalAssemblyResolver(null, false, ".NETCoreApp,Version=v10.0"), settings);
        var lowered = Tidy(decompiler.DecompileTypesAsString(types));

        var il = new PlainTextOutput();
        var disassembler = new ReflectionDisassembler(il, CancellationToken.None);
        foreach (var type in types) disassembler.DisassembleType(file, type);
        var ilText = Tidy(il.ToString());

        foreach (var (name, text) in new[] { ("lowered C#", lowered), ("IL", ilText) })
            if (text.Count(c => c == '\n') + 1 > MaxLines)
                return (null, $"its {name} is over {MaxLines} lines; trim the snippet to what the step is about.");
        return (new LoweredCode(lowered, ilText), null);
    }

    /// <summary>A type the author wrote or the compiler made for the code (not the module, the private-implementation holder or an embedded attribute).</summary>
    private static bool IsAuthored(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var name = metadata.GetString(type.Name);
        var ns = metadata.GetString(type.Namespace);
        return name is not ("<Module>" or "<PrivateImplementationDetails>")
            && !ns.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
            && !(ns == "System.Runtime.CompilerServices" && name.EndsWith("Attribute", StringComparison.Ordinal));
    }

    /// <summary>Normalises what the printers emit so the text is the same on every machine and fits a phone: LF endings, spaces for tabs, no trailing blanks.</summary>
    private static string Tidy(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.Replace("\t", "    ").TrimEnd())
            // The decompiler annotates the explicit interface implementations it synthesises; that is its note, not the compiler's output.
            .Where(l => !l.TrimStart().StartsWith("//ILSpy generated", StringComparison.Ordinal))
            // Addresses and header sizes describe the file layout, not the code.
            .Where(l => !l.TrimStart().StartsWith("// Method begins at RVA", StringComparison.Ordinal)
                     && !l.TrimStart().StartsWith("// Header size:", StringComparison.Ordinal));
        var result = new StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0) { blank = result.Length > 0; continue; }
            if (blank) result.Append('\n');
            blank = false;
            if (result.Length > 0) result.Append('\n');
            result.Append(line);
        }
        return result.ToString();
    }
}
