using System.Runtime.CompilerServices;
using Brilliant.Cli;

namespace Brilliant.Cli.Tests;

/// <summary>The lowering service: what the compiler makes of a snippet, pinned with golden files (#154).</summary>
public sealed class LoweringTests
{
    private static string Directory_([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Lowering");

    public static TheoryData<string> Fixtures() =>
        new(Directory.GetFiles(Directory_(), "*.snippet").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).ToArray());

    private static LoweredCodeOrError Lower(string name) =>
        new(CSharpLowering.Lower(File.ReadAllText(Path.Combine(Directory_(), name + ".snippet"))));

    private sealed record LoweredCodeOrError((Brilliant.Core.Content.LoweredCode? Lowered, string? Error) Result);

    // A change to what is shown is a change to every pack, so it shows up as a diff here. Rewrite with UPDATE_GOLDENS=1 after reading it.
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_lowered_csharp_and_il_match_their_golden_files(string name)
    {
        var (lowered, error) = Lower(name).Result;
        Assert.True(lowered is not null, error);
        foreach (var (extension, text) in new[] { (".lowered.txt", lowered.CSharp), (".il", lowered.Il) })
        {
            var golden = Path.Combine(Directory_(), name + extension);
            if (Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1") File.WriteAllText(golden, text + "\n");
            Assert.True(File.Exists(golden), $"No golden file {name}{extension}; run with UPDATE_GOLDENS=1 to write it.");
            Assert.Equal(File.ReadAllText(golden).ReplaceLineEndings("\n"), text + "\n");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Lowering_the_same_snippet_again_gives_the_same_text(string name) =>
        Assert.Equal(Lower(name).Result, Lower(name).Result);
}
