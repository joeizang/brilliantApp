using System.Reflection;
using System.Runtime.Loader;
using Basic.Reference.Assemblies;
using Brilliant.Code.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Brilliant.Recording.Spike;

/// <summary>Roslyn plumbing shared by the three experiments: compile in memory, emit to bytes, load into a collectible context.</summary>
static class Compile
{
    static readonly CSharpParseOptions Parse = new(LanguageVersion.CSharp14);

    public static readonly IReadOnlyList<MetadataReference> References =
        [.. Net100.References.All, MetadataReference.CreateFromFile(typeof(ProbeBudget).Assembly.Location)];

    public static SyntaxTree Tree(string source, string path = "example.cs") => CSharpSyntaxTree.ParseText(source, Parse, path);

    public static CSharpCompilation Compilation(IEnumerable<SyntaxTree> trees, OptimizationLevel level = OptimizationLevel.Debug, string name = "Recorded") =>
        CSharpCompilation.Create(name, trees, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: level, deterministic: true, nullableContextOptions: NullableContextOptions.Enable));

    public static byte[] Emit(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
            throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }

    public static (AssemblyLoadContext Context, Assembly Assembly) Load(byte[] image)
    {
        var context = new AssemblyLoadContext("recorded", isCollectible: true);
        return (context, context.LoadFromStream(new MemoryStream(image)));
    }
}
