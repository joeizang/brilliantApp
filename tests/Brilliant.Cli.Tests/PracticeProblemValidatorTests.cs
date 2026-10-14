using Brilliant.Cli;
using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

/// <summary>A lesson's <c>practice.yaml</c>: practice-only problems, tagged with a pattern, outside the lesson (docs/practice-mode.md).</summary>
public sealed class PracticeProblemValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brilliant-tests-" + Guid.NewGuid().ToString("N"));

    public PracticeProblemValidatorTests()
    {
        Directory.CreateDirectory(_root);
        Write("pack.yaml", "id: pack.t\nversion: 1.0.0\n");
        Write("tracks/t/track.yaml", "id: track.t\ntitle: T\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private const string PrefixSums = "patterns:\n  - id: pattern.prefix-sums\n    title: Prefix sums\n";

    private static string Lesson(string id, string head = "") =>
        $"id: lesson.{id}\ntitle: {id}\n" + head + "steps:\n"
        + $"  - id: step.{id}.e\n    type: explain\n    title: E\n    body: Hello\n";

    /// <summary>A Parsons problem (no reference run needed), tagged unless <paramref name="tag"/> says otherwise.</summary>
    private static string Problem(string id, string type = "parsons", string tag = "pattern.prefix-sums") =>
        $"  - id: step.{id}\n    type: {type}\n    title: P\n    prompt: Put these in order\n"
        + (type == "parsons" ? "    solution: |\n      x = 1\n      y = 2\n" : "    body: Hello\n")
        + (tag.Length > 0 ? $"    pattern: {tag}\n    patternCue: Many range sums\n" : "");

    private List<string> Errors() => ContentValidator.Validate(_root).Errors.Select(e => e.ToString()).ToList();

    [Fact]
    public void Practice_problems_load_into_the_lesson_but_not_its_steps()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums));
        Write("tracks/t/l1/practice.yaml", "problems:\n" + Problem("one.extra"));
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var lesson = ContentPackFormat.Load(outPath).Get<Lesson>("lesson.one");
        Assert.Equal("step.one.e", Assert.Single(lesson.Steps).Id);
        var problem = Assert.Single(lesson.Practice);
        Assert.Equal(("step.one.extra", "pattern.prefix-sums"), (problem.Id, problem.Pattern));
    }

    [Fact]
    public void A_lesson_without_practice_yaml_has_no_practice_problems()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums));

        Assert.Empty(Errors());
    }

    [Fact]
    public void A_practice_problem_needs_a_pattern()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums));
        Write("tracks/t/l1/practice.yaml", "problems:\n" + Problem("one.extra", tag: ""));

        var error = Assert.Single(Errors());
        Assert.Contains("tracks/t/l1/practice.yaml", error);
        Assert.Contains("problems[0] ('step.one.extra'): a practice problem needs a 'pattern'", error);
    }

    [Fact]
    public void A_practice_problem_is_one_the_learner_solves()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums));
        Write("tracks/t/l1/practice.yaml", "problems:\n" + Problem("one.extra", type: "explain", tag: ""));

        Assert.Contains(Errors(), e => e.Contains("a practice problem is one the learner solves: write-code, fill-blank or parsons (found 'explain')"));
    }

    [Fact]
    public void A_practice_problem_cannot_name_a_pattern_a_later_lesson_teaches()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one"));
        Write("tracks/t/l1/practice.yaml", "problems:\n" + Problem("one.extra"));
        Write("tracks/t/l2/lesson.yaml", Lesson("two", PrefixSums));

        var error = Assert.Single(Errors());
        Assert.Contains("tracks/t/l1/practice.yaml", error);
        Assert.Contains("is taught by a later lesson ('lesson.two')", error);
    }

    [Fact]
    public void A_practice_problem_id_must_be_unique_across_the_pack()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums));
        Write("tracks/t/l1/practice.yaml", "problems:\n" + Problem("one.e"));

        Assert.Contains(Errors(), e => e.Contains("duplicate id 'step.one.e'"));
    }

    [Theory]
    [InlineData("problems: []\n", "'problems' is empty")]
    [InlineData("problems:\n  -\n", "problems[0]: entry is empty")]
    [InlineData("steps: []\n", "line")]
    public void A_malformed_practice_file_is_rejected(string yaml, string expected)
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums));
        Write("tracks/t/l1/practice.yaml", yaml);

        Assert.Contains(Errors(), e => e.Contains(expected));
    }
}
