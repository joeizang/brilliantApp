using Brilliant.Core.Content;

namespace Brilliant.Core.Tests;

public class ContentPackTests
{
    private static readonly Comparison Cmp = new("var x = 1;", "x = 1");

    private static MemoryStream BuildPack(IEnumerable<Track>? tracks = null, IEnumerable<Lesson>? lessons = null)
    {
        var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            tracks ?? [new Track("track.a", "A", ["lesson.one"])],
            lessons ??
            [new Lesson("lesson.one", "One", "track.a",
                [new ExplainStep("step.one.intro", "Intro", "Body", [new CodeSnippet("python", "print(1)")], Cmp)])]);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Loads_pack_and_looks_items_up_by_stable_id()
    {
        var graph = ContentPackFormat.Load(BuildPack());

        Assert.Equal("1.0.0", graph.Manifest.Version);
        Assert.Equal("A", graph.Get<Track>("track.a").Title);
        Assert.Equal("One", graph.Get<Lesson>("lesson.one").Title);

        var step = Assert.IsType<ExplainStep>(graph.Get<Step>("step.one.intro"));
        Assert.Equal("Body", step.Body);
        Assert.Equal("print(1)", step.Snippets.Single().Code);
        Assert.Equal(Cmp, step.Comparison);
    }

    [Fact]
    public void Lookup_of_unknown_or_wrongly_typed_id_fails()
    {
        var graph = ContentPackFormat.Load(BuildPack());

        Assert.False(graph.TryGet<Lesson>("lesson.missing", out _));
        Assert.False(graph.TryGet<Lesson>("track.a", out _));
        Assert.Throws<KeyNotFoundException>(() => graph.Get<Step>("lesson.one"));
    }

    [Fact]
    public void Duplicate_ids_are_rejected()
    {
        var lessons = new[]
        {
            new Lesson("lesson.one", "One", "track.a", [new ExplainStep("step.x", "X", "b", [], null)]),
            new Lesson("lesson.two", "Two", "track.a", [new ExplainStep("step.x", "X", "b", [], null)]),
        };
        var ex = Assert.Throws<ContentPackException>(() => ContentPackFormat.Load(BuildPack(lessons: lessons)));
        Assert.Contains("step.x", ex.Message);
    }

    [Fact]
    public void Garbage_is_reported_as_content_pack_error()
    {
        var ms = new MemoryStream("not a zip"u8.ToArray());
        Assert.Throws<ContentPackException>(() => ContentPackFormat.Load(ms));
    }

    [Fact]
    public void Unsupported_format_version_is_rejected()
    {
        var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("p", "1.0.0", 99), [], []);
        ms.Position = 0;
        var ex = Assert.Throws<ContentPackException>(() => ContentPackFormat.Load(ms));
        Assert.Contains("99", ex.Message);
    }
}
