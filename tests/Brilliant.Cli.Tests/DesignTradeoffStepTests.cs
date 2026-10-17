using System.Text.RegularExpressions;
using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

/// <summary>The <c>design-tradeoff</c> step as authors write it: options, a weighted rubric, a model answer and red flags, checked at pack time (#165).</summary>
public sealed class DesignTradeoffStepTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "brilliant-tradeoff-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_repository, "content", "sample");
    private string AppManifest => Path.Combine(_repository, "apps", "sample", "manifest.yaml");

    private const string Cache = """
          - id: step.sample.cache
            type: design-tradeoff
            title: Where should the cache live?
            prompt: Three instances serve one API. Where does the cache go?
            options:
              - text: In-process MemoryCache
                correct: false
                feedback: Each instance caches its own copy, so they disagree.
              - text: Redis shared by all instances
                correct: true
                feedback: One copy to invalidate, at the cost of a network hop.
            rubric:
              - point: Several instances need one consistent view
                points: 2
              - point: Accepts the network hop as the price
            modelAnswer: Use a shared cache, because consistency matters more than the saved hop.
            redFlags:
              - A shared cache is always faster than in-process memory.
            hints:
              - Ask what happens when two instances cache the same key.
        """;

    private void WritePack(string steps, string stepTypes = "[explain, design-tradeoff]", string? reviewItems = null)
    {
        Write("apps/sample/manifest.yaml", $$"""
            appId: example.tradeoff
            displayName: Tradeoff
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: sample.pack.zip
            runtimes: [csharp]
            stepTypes: {{stepTypes}}
            """);
        Write("content/sample/pack.yaml", "id: pack.tradeoff\nversion: 1.0.0\ntargetFramework: net10.0\nlangVersion: '14'\n");
        Write("content/sample/tracks/one/track.yaml", "id: track.tradeoff\ntitle: Tradeoff\n");
        var review = reviewItems is null ? "" : "concepts:\n  - id: concept.sample\n    title: Sample concept\nreviewItems:\n" + reviewItems + "\n";
        Write("content/sample/tracks/one/tradeoff/lesson.yaml", "id: lesson.tradeoff\ntitle: Tradeoff\n" + review + "steps:\n" + steps + "\n");
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() { try { Directory.Delete(_repository, true); } catch (IOException) { } }

    private IReadOnlyList<string> Errors() =>
        ContentValidator.Validate(Root, appManifestPath: AppManifest).Errors.Select(e => e.Message).ToList();

    private static string DropLines(string yaml, string containing) =>
        string.Join('\n', yaml.Split('\n').Where(l => !l.Contains(containing)));

    [Fact]
    public void A_tradeoff_with_options_a_rubric_and_a_model_answer_is_valid()
    {
        WritePack(Cache);

        Assert.Empty(Errors());
    }

    [Fact]
    public void The_step_packs_with_its_options_weighted_rubric_model_answer_and_red_flags()
    {
        WritePack(Cache);

        var bytes = ContentPacker.PackToBytes(Root, out var report, appManifestPath: AppManifest);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<DesignTradeoffStep>(ContentPackFormat.Load(new MemoryStream(bytes!)).Lessons.Single().Steps.Single());
        Assert.Equal(2, step.Options.Count);
        Assert.True(step.Options[1].Correct);
        Assert.Equal([2, 1], step.Rubric.Select(p => p.Points));
        Assert.StartsWith("Use a shared cache", step.ModelAnswer);
        Assert.Single(step.RedFlags);
        Assert.Single(step.Hints);
        Assert.True(DesignTradeoffEvaluator.Evaluate(step, 1, [0, 1], []).IsCorrect);
    }

    [Fact]
    public void A_tradeoff_can_be_a_review_question()
    {
        WritePack(Cache, reviewItems: "  - id: review.sample.cache\n    concept: concept.sample\n    step: step.sample.cache");

        Assert.Empty(Errors());
    }

    [Fact]
    public void The_app_manifest_must_enable_the_step_type()
    {
        WritePack(Cache, stepTypes: "[explain]");

        Assert.Contains(Errors(), e => e.Contains("step type 'design-tradeoff' is not enabled"));
    }

    [Fact]
    public void Red_flags_are_optional()
    {
        WritePack(Regex.Replace(Cache, @"(?s)    redFlags:.*?(?=    hints:)", ""));

        Assert.Empty(Errors());
    }

    [Fact]
    public void The_prompt_and_model_answer_are_required()
    {
        WritePack(DropLines(Cache, "prompt:"));
        Assert.Contains(Errors(), e => e.Contains("'prompt' is required"));

        WritePack(DropLines(Cache, "modelAnswer:"));
        Assert.Contains(Errors(), e => e.Contains("'modelAnswer' is required"));
    }

    [Fact]
    public void Exactly_one_option_must_be_correct()
    {
        WritePack(Cache.Replace("correct: true", "correct: false"));
        Assert.Contains(Errors(), e => e.Contains("no option is marked 'correct: true'"));

        WritePack(Cache.Replace("correct: false", "correct: true"));
        Assert.Contains(Errors(), e => e.Contains("exactly one option"));
    }

    [Fact]
    public void There_must_be_two_to_five_options_each_with_text_and_feedback()
    {
        WritePack(Regex.Replace(Cache, @"(?s)      - text: In-process.*?(?=      - text: Redis)", ""));
        Assert.Contains(Errors(), e => e.Contains("needs 2 to 5 options"));

        WritePack(DropLines(Cache, "feedback: Each instance"));
        Assert.Contains(Errors(), e => e.Contains("options[0] needs 'feedback'"));
    }

    [Fact]
    public void The_rubric_needs_two_to_six_points_each_with_text()
    {
        WritePack(Regex.Replace(Cache, @"(?s)      - point: Accepts.*?(?=    modelAnswer)", ""));
        Assert.Contains(Errors(), e => e.Contains("'rubric' needs 2 to 6 points"));

        WritePack(Cache.Replace("- point: Accepts the network hop as the price", "- point: ''"));
        Assert.Contains(Errors(), e => e.Contains("rubric[1] needs 'point'"));
    }

    [Fact]
    public void A_rubric_points_weight_defaults_to_one_and_stays_between_one_and_five()
    {
        WritePack(Cache.Replace("points: 2", "points: 0"));
        Assert.Contains(Errors(), e => e.Contains("rubric[0]: 'points' must be between 1 and 5"));

        WritePack(Cache.Replace("points: 2", "points: 6"));
        Assert.Contains(Errors(), e => e.Contains("rubric[0]: 'points' must be between 1 and 5"));
    }

    [Fact]
    public void At_most_four_red_flags_and_none_blank()
    {
        WritePack(Cache.Replace("      - A shared cache is always faster than in-process memory.\n", string.Concat(Enumerable.Range(0, 5).Select(i => $"      - Claim {i}\n"))));
        Assert.Contains(Errors(), e => e.Contains("at most 4 redFlags"));

        WritePack(Cache.Replace("- A shared cache is always faster than in-process memory.", "- ''"));
        Assert.Contains(Errors(), e => e.Contains("redFlags[0] is blank"));
    }

    [Fact]
    public void Fields_that_belong_to_other_step_types_are_rejected()
    {
        WritePack(Cache.Replace("    hints:", "    defects:\n      - line: 1\n    hints:"));

        Assert.Contains(Errors(), e => e.Contains("a design-tradeoff step is answered by picking an option"));
    }

    [Fact]
    public void Hints_are_allowed_on_a_tradeoff()
    {
        WritePack(Cache);

        Assert.DoesNotContain(Errors(), e => e.Contains("hints"));
    }
}
