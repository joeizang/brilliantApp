using System.Text.Json;
using Brilliant.Core.Content;

namespace Brilliant.Core.Tests;

public class ComparisonTests
{
    [Fact]
    public void Full_legacy_format_one_packs_load_their_original_comparison_objects()
    {
        var bytes = Packs.Raw("""
            {"tracks":[{"id":"track.one","title":"One","lessonIds":["lesson.one"]}],
             "lessons":[{"id":"lesson.one","title":"One","trackId":"track.one","steps":[
              {"type":"explain","id":"step.one","title":"One","body":"Body","snippets":[],
               "comparison":{"cSharp":"var x = 1;\n","python":"x = 1\n"}}]}]}
            """);

        var graph = ContentPackFormat.Load(new MemoryStream(bytes));

        Assert.Equal(1, graph.Manifest.FormatVersion);
        var step = Assert.IsType<ExplainStep>(graph.Get<Step>("step.one"));
        Assert.Equal([new LabelledSnippet("C#", "csharp", "var x = 1;\n"), new("Python", "python", "x = 1\n")], step.Comparison);
    }

    [Fact]
    public void Legacy_cached_comparisons_load_with_their_original_order_and_labels()
    {
        const string json = """{"id":"step.one","title":"One","body":"Body","snippets":[],"comparison":{"cSharp":"var x = 1;\n","python":"x = 1\n"}}""";

        var step = JsonSerializer.Deserialize<ExplainStep>(json, ContentPackFormat.JsonOptions)!;

        Assert.Equal([new LabelledSnippet("C#", "csharp", "var x = 1;\n"), new("Python", "python", "x = 1\n")], step.Comparison);
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(step, ContentPackFormat.JsonOptions));
        Assert.Equal(JsonValueKind.Array, written.RootElement.GetProperty("comparison").ValueKind);
    }

    [Fact]
    public void Labelled_comparisons_keep_arbitrary_languages_labels_order_and_code_through_a_pack()
    {
        LabelledSnippet[] snippets = [new("Before", "csharp", "return xs.ToArray();\n"), new("After", "csharp", "return [.. xs];\n"), new("SQL equivalent", "sql", "SELECT * FROM xs;\n")];
        var step = new ExplainStep("step.one", "One", "Body", [], snippets);
        using var pack = new MemoryStream();
        ContentPackFormat.Write(pack, new("pack.one", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            [new("track.one", "One", ["lesson.one"])], [new("lesson.one", "One", "track.one", [step])]);
        pack.Position = 0;

        using (var zip = new System.IO.Compression.ZipArchive(pack, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true))
        {
            using var manifest = JsonDocument.Parse(zip.GetEntry(ContentPackFormat.ManifestEntry)!.Open());
            Assert.Equal(2, manifest.RootElement.GetProperty("formatVersion").GetInt32());
            using var content = JsonDocument.Parse(zip.GetEntry(ContentPackFormat.ContentEntry)!.Open());
            Assert.Equal(JsonValueKind.Array, content.RootElement.GetProperty("lessons")[0].GetProperty("steps")[0].GetProperty("comparison").ValueKind);
        }
        pack.Position = 0;

        var loaded = Assert.IsType<ExplainStep>(ContentPackFormat.Load(pack).Get<Step>("step.one"));

        Assert.Equal(snippets, loaded.Comparison);
    }

    [Fact]
    public void Absent_comparisons_remain_absent_in_old_packs()
    {
        const string json = """{"id":"step.one","title":"One","body":"Body","snippets":[],"comparison":null}""";

        Assert.Null(JsonSerializer.Deserialize<ExplainStep>(json, ContentPackFormat.JsonOptions)!.Comparison);
    }
}
