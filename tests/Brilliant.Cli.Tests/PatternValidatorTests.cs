using Brilliant.Cli;
using Brilliant.Core.Content;
using Brilliant.Core.Python;

namespace Brilliant.Cli.Tests;

/// <summary>Lessons declare the patterns they teach; problem steps name the pattern that solves them (docs/practice-mode.md).</summary>
public sealed class PatternValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brilliant-tests-" + Guid.NewGuid().ToString("N"));

    public PatternValidatorTests()
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

    /// <summary>A lesson with one Parsons problem (no reference run needed), optionally tagged and with extra lesson-level YAML.</summary>
    private static string Lesson(string id, string head = "", string stepExtra = "", string prompt = "Put these in order", string type = "parsons") =>
        $"id: lesson.{id}\ntitle: {id}\n" + head + "steps:\n"
        + $"  - id: step.{id}.p\n    type: {type}\n    title: P\n    prompt: {prompt}\n"
        + (type == "parsons" ? "    solution: |\n      x = 1\n      y = 2\n" : "    options:\n      - text: a\n        correct: true\n      - text: b\n")
        + stepExtra;

    private ValidationReport Validate() => ContentValidator.Validate(_root);

    private List<string> Errors() => Validate().Errors.Select(e => e.ToString()).ToList();

    [Fact]
    public void Declared_patterns_and_step_tags_load_into_the_pack()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums, "    pattern: pattern.prefix-sums\n"));
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        Assert.Empty(report.Warnings);
        var graph = ContentPackFormat.Load(outPath);
        Assert.Equal("Prefix sums", Assert.Single(graph.Get<Lesson>("lesson.one").Patterns).Title);
        Assert.Equal("pattern.prefix-sums", graph.Get<Step>("step.one.p").Pattern);
    }

    [Fact]
    public void A_step_may_name_a_pattern_from_an_earlier_lesson_or_another_track()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums));
        Write("tracks/t/l2/lesson.yaml", Lesson("two", stepExtra: "    pattern: pattern.prefix-sums\n"));
        Write("tracks/u/track.yaml", "id: track.u\ntitle: U\n");
        Write("tracks/u/l1/lesson.yaml", Lesson("three", stepExtra: "    pattern: pattern.prefix-sums\n"));

        Assert.Empty(Errors());
    }

    [Fact]
    public void A_pattern_no_lesson_declares_is_rejected()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", stepExtra: "    pattern: pattern.nowhere\n"));

        var error = Assert.Single(Errors());
        Assert.Contains("tracks/t/l1/lesson.yaml", error);
        Assert.Contains("steps[0] ('step.one.p'): pattern 'pattern.nowhere' is not declared", error);
    }

    [Fact]
    public void A_pattern_taught_by_a_later_lesson_of_the_same_track_is_rejected()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", stepExtra: "    pattern: pattern.prefix-sums\n"));
        Write("tracks/t/l2/lesson.yaml", Lesson("two", PrefixSums));

        var error = Assert.Single(Errors());
        Assert.Contains("is taught by a later lesson ('lesson.two')", error);
    }

    [Fact]
    public void A_pattern_declared_twice_is_a_duplicate_id()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums));
        Write("tracks/t/l2/lesson.yaml", Lesson("two", PrefixSums));

        var error = Assert.Single(Errors());
        Assert.Contains("duplicate id 'pattern.prefix-sums'", error);
    }

    [Theory]
    [InlineData("- id: prefix-sums\n    title: Prefix sums\n", "id 'prefix-sums' is invalid")]
    [InlineData("- id: pattern.prefix-sums\n", "'title' is required")]
    [InlineData("-\n", "patterns[0]: entry is empty")]
    public void A_malformed_pattern_declaration_is_rejected(string entry, string expected)
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", "patterns:\n  " + entry));

        Assert.Contains(Errors(), e => e.Contains(expected));
    }

    [Fact]
    public void Only_problems_the_learner_solves_carry_a_pattern()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums, "    pattern: pattern.prefix-sums\n", type: "choice"));

        Assert.Contains(Errors(), e => e.Contains("'pattern' only applies to problems the learner solves (write-code, fill-blank or parsons)"));
    }

    [Fact]
    public void A_phone_alternative_takes_its_pattern_from_its_step()
    {
        var write = "id: lesson.one\ntitle: One\n" + PrefixSums + "steps:\n"
            + "  - id: step.one.w\n    type: write-code\n    title: W\n    prompt: Write it\n    language: python\n    entrypoint: f\n"
            + "    solution: |\n      def f():\n          return 1\n    tests:\n      - input: \"\"\n        expected: \"1\"\n"
            + "    largeScreenPreferred: true\n    pattern: pattern.prefix-sums\n"
            + "    phoneAlternative:\n      id: step.one.w.arrange\n      type: parsons\n      title: A\n      prompt: Arrange\n"
            + "      solution: |\n        def f():\n            return 1\n      pattern: pattern.prefix-sums\n";
        Write("tracks/t/l1/lesson.yaml", write);

        Assert.Contains(Errors(), e => e.Contains("a phone alternative is solved by the same pattern as its step"));
    }

    [Fact]
    public void An_empty_pattern_is_rejected()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", stepExtra: "    pattern: \"\"\n"));

        Assert.Contains(Errors(), e => e.Contains("'pattern' is empty"));
    }

    [Fact]
    public void A_prompt_that_names_its_own_pattern_is_a_warning_not_an_error()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums, "    pattern: pattern.prefix-sums\n", prompt: "Use prefix sums to answer"));

        var report = Validate();

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var warning = Assert.Single(report.Warnings);
        Assert.Equal("tracks/t/l1/lesson.yaml", warning.File);
        Assert.Contains("the prompt names its own pattern ('Prefix sums')", warning.Message);
    }

    [Fact]
    public void A_phone_alternative_prompt_that_names_the_pattern_is_a_warning_too()
    {
        var write = "id: lesson.one\ntitle: One\n" + PrefixSums + "steps:\n"
            + "  - id: step.one.w\n    type: write-code\n    title: W\n    prompt: Write it\n    language: python\n    entrypoint: f\n"
            + "    solution: |\n      def f():\n          return 1\n    tests:\n      - input: \"\"\n        expected: \"1\"\n"
            + "    largeScreenPreferred: true\n    pattern: pattern.prefix-sums\n"
            + "    phoneAlternative:\n      id: step.one.w.arrange\n      type: parsons\n      title: A\n      prompt: Arrange it with prefix sums\n"
            + "      solution: |\n        def f():\n            return 1\n";
        Write("tracks/t/l1/lesson.yaml", write);

        var report = ContentValidator.Validate(_root, new PassingRunner());

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        Assert.Contains("the prompt of the phone alternative names its own pattern", Assert.Single(report.Warnings).Message);
    }

    [Fact]
    public void A_pattern_title_split_across_lines_of_the_prompt_is_still_found()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums, "    pattern: pattern.prefix-sums\n", prompt: "|\n      Use prefix\n      sums to answer"));

        Assert.Single(Validate().Warnings);
    }

    [Fact]
    public async Task Warnings_are_printed_and_do_not_fail_validate()
    {
        Write("tracks/t/l1/lesson.yaml", Lesson("one", PrefixSums, "    pattern: pattern.prefix-sums\n", prompt: "Use prefix sums to answer"));
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await CliApp.RunAsync(["validate", _root], output, error, new HttpClient(), new NoCredentials());

        Assert.Equal(0, code);
        Assert.Contains("warning: tracks/t/l1/lesson.yaml: steps[0]", error.ToString());
        Assert.Contains("OK:", output.ToString());
    }

    private sealed class PassingRunner : IReferenceSolutionRunner
    {
        public TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests) => new(TestRunStatus.Passed, "", null, null, null, []);
        public TraceResult Trace(string code, IReadOnlyList<Visual> visuals) => throw new NotSupportedException();
    }

    private sealed class NoCredentials : Brilliant.Core.Sync.IDeviceCredentialStore
    {
        public Task<Brilliant.Core.Sync.DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult<Brilliant.Core.Sync.DeviceCredentials?>(null);
        public Task SaveAsync(Brilliant.Core.Sync.DeviceCredentials credentials, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
