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
    public void A_step_keeps_its_large_screen_flag_and_phone_alternative_through_a_pack()
    {
        var alternative = new ParsonsStep("step.one.write.arrange", "Arrange", "P", "python", [new("def f():", 0), new("return 1", 1)]);
        var write = new WriteCodeStep("step.one.write", "Write", "P", "python", "", "f", [new CodeTest("", "1")])
        {
            LargeScreenPreferred = true,
            PhoneAlternative = alternative,
        };
        var graph = ContentPackFormat.Load(BuildPack(lessons: [new Lesson("lesson.one", "One", "track.a", [write])]));

        var loaded = Assert.IsType<WriteCodeStep>(graph.Get<Lesson>("lesson.one").Steps.Single());
        Assert.True(loaded.LargeScreenPreferred);
        var alt = Assert.IsType<ParsonsStep>(loaded.PhoneAlternative);
        Assert.Equal("step.one.write.arrange", alt.Id);
        Assert.Equal(["def f():", "return 1"], alt.Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_lessons_patterns_and_a_steps_pattern_survive_a_pack()
    {
        var write = new WriteCodeStep("step.one.write", "Write", "P", "python", "", "f", [new CodeTest("", "1")]) { Pattern = "pattern.prefix-sums" };
        var lesson = new Lesson("lesson.one", "One", "track.a", [write]) { Patterns = [new Pattern("pattern.prefix-sums", "Prefix sums")] };

        var graph = ContentPackFormat.Load(BuildPack(lessons: [lesson]));

        Assert.Equal("Prefix sums", graph.Get<Pattern>("pattern.prefix-sums").Title);
        Assert.Equal(["pattern.prefix-sums"], graph.Get<Lesson>("lesson.one").Patterns.Select(p => p.Id));
        Assert.Equal("pattern.prefix-sums", graph.Get<Step>("step.one.write").Pattern);
    }

    [Fact]
    public void A_step_whose_pattern_is_not_in_the_pack_is_refused()
    {
        var write = new WriteCodeStep("step.one.write", "Write", "P", "python", "", "f", [new CodeTest("", "1")]) { Pattern = "pattern.missing" };

        var ex = Assert.Throws<ContentPackException>(() => ContentPackFormat.Load(BuildPack(lessons: [new Lesson("lesson.one", "One", "track.a", [write])])));

        Assert.Contains("pattern.missing", ex.Message);
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
