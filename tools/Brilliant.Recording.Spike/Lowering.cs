using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using Microsoft.CodeAnalysis;

namespace Brilliant.Recording.Spike;

/// <summary>
/// Experiment 2. Roslyn emits the example to an in-memory PE; ICSharpCode.Decompiler turns it back into C# with the
/// async / foreach / using reconstruction switched off (so the compiler-generated state machine and enumerator stay visible),
/// and the ILSpy disassembler prints the IL.
/// </summary>
static class Lowering
{
    public sealed record Output(string LoweredCSharp, string Il, double EmitMs, double DecompileMs, double DisassembleMs, string ImageSha256);

    public static Output Lower(string source)
    {
        var sw = Stopwatch.StartNew();
        // Release: the state machine is a struct and locals keep their source names less often, as in a shipped build.
        var image = Compile.Emit(Compile.Compilation([Compile.Tree(source)], OptimizationLevel.Release, "Lowered"));
        var emitMs = sw.Elapsed.TotalMilliseconds;

        using var peFile = new ICSharpCode.Decompiler.Metadata.PEFile("Lowered.dll", new MemoryStream(image), PEStreamOptions.PrefetchEntireImage);
        var settings = new DecompilerSettings(LanguageVersion.CSharp1)
        {
            // CSharp1 turns most reconstructions off; these are the ones it leaves on and that would hide the lowering.
            ForEachStatement = false, UsingStatement = false, LockStatement = false, AsyncEnumerator = false,
            ShowXmlDocumentation = false,
            ThrowOnAssemblyResolveErrors = false,
            AutoLoadAssemblyReferences = false,
        };
        sw.Restart();
        var resolver = new UniversalAssemblyResolver(null, false, ".NETCoreApp,Version=v10.0");
        var decompiler = new CSharpDecompiler(peFile, resolver, settings);
        var lowered = decompiler.DecompileWholeModuleAsString();
        var decompileMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var il = new PlainTextOutput();
        new ReflectionDisassembler(il, CancellationToken.None).WriteModuleContents(peFile);
        var disassembleMs = sw.Elapsed.TotalMilliseconds;

        return new Output(lowered, il.ToString(), emitMs, decompileMs, disassembleMs, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)));
    }
}
