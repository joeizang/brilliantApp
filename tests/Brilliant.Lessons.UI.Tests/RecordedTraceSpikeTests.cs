using Brilliant.Core.Code;
using Brilliant.Core.Content;
using Bunit;
using Microsoft.JSInterop;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>
/// Spike #139: a trace recorded by tools/Brilliant.Recording.Spike (Roslyn probes, run once, frozen to JSON) is fed to the
/// existing, unmodified trace player the same way TraceRun does after a live run: TraceModel.Build, then TracePlayer.
/// </summary>
public class RecordedTraceSpikeTests : ShortcutContext
{
    static string Samples()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(dir.FullName, "tools")) ) dir = dir.Parent!;
        return Path.Combine(dir.FullName, "tools", "Brilliant.Recording.Spike", "samples");
    }

    [Fact]
    public void A_recorded_insertion_sort_plays_in_the_existing_trace_player()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/code-editor.js");
        module.Setup<int>("create", _ => true).SetResult(1);

        var result = TraceResultJson.Parse(File.ReadAllText(Path.Combine(Samples(), "trace.json")));
        var code = File.ReadAllText(Path.Combine(Samples(), "example.cs")).TrimEnd('\n');
        var visuals = new[] { new Visual("a", Visual.Array, ["i", "j"]) };

        var cut = Render<TracePlayer>(p => p.Add(c => c.Language, "csharp").Add(c => c.Code, code)
            .Add(c => c.States, TraceModel.Build(result, visuals)).Add(c => c.Result, result));
        cut.WaitForAssertion(() => module.VerifyInvoke("highlightLine"));

        Assert.Equal("Step 1 of 25", cut.Find(".position").TextContent);
        for (var i = 0; i < 24; i++) cut.Find("button.next").Click();
        Assert.Equal("Step 25 of 25", cut.Find(".position").TextContent);
        Assert.Equal(["1", "2", "4", "5"], cut.FindAll("g.cell .cell-text").Select(t => t.TextContent));
    }
}
