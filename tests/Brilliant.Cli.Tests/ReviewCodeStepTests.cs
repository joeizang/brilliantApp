using System.Text.RegularExpressions;
using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

/// <summary>The <c>review-code</c> step as authors write it: planted defects and decoys checked against the shown code at pack time (#164).</summary>
public sealed class ReviewCodeStepTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "brilliant-review-code-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_repository, "content", "sample");
    private string AppManifest => Path.Combine(_repository, "apps", "sample", "manifest.yaml");

    private const string Cache = """
          - id: step.sample.cache
            type: review-code
            title: Review the cache
            prompt: A teammate added a cache to the lookup service. Flag what you would push back on.
            language: csharp
            code: |
              public sealed class Cache
              {
                  private readonly Dictionary<string, string> _items = new();
                  public string Get(string key)
                  {
                      var found = _items.ContainsKey(key);
                      if (found = true) return _items[key];
                      return Load(key);
                  }
              }
            defects:
              - line: 3
                category: concurrency
                explanation: A plain Dictionary is not safe for concurrent readers and writers.
              - line: 7
                category: bug
                explanation: '`=` assigns, so this is always true and throws on a miss.'
            decoys:
              - line: 8
                explanation: Calling Load on a miss is the whole point of a cache.
            hints:
              - Read each line as if two requests ran it at once.
        """;

    private void WritePack(string steps, string stepTypes = "[explain, review-code]", string? reviewItems = null)
    {
        Write("apps/sample/manifest.yaml", $$"""
            appId: example.review
            displayName: Review
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: sample.pack.zip
            runtimes: [csharp]
            stepTypes: {{stepTypes}}
            """);
        Write("content/sample/pack.yaml", "id: pack.review\nversion: 1.0.0\ntargetFramework: net10.0\nlangVersion: '14'\n");
        Write("content/sample/tracks/one/track.yaml", "id: track.review\ntitle: Review\n");
        var review = reviewItems is null ? "" : "concepts:\n  - id: concept.sample\n    title: Sample concept\nreviewItems:\n" + reviewItems + "\n";
        Write("content/sample/tracks/one/review/lesson.yaml", "id: lesson.review\ntitle: Review\n" + review + "steps:\n" + steps + "\n");
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

    [Fact]
    public void A_review_with_defects_and_a_decoy_is_valid()
    {
        WritePack(Cache);

        Assert.Empty(Errors());
    }

    [Fact]
    public void The_step_packs_with_its_code_defects_and_decoys()
    {
        WritePack(Cache);

        var bytes = ContentPacker.PackToBytes(Root, out var report, appManifestPath: AppManifest);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<ReviewCodeStep>(ContentPackFormat.Load(new MemoryStream(bytes!)).Lessons.Single().Steps.Single());
        Assert.Equal("csharp", step.Language);
        Assert.Equal(10, step.Lines.Count);
        Assert.Equal([(3, "concurrency"), (7, "bug")], step.Defects.Select(d => (d.Line, d.Category)));
        Assert.All(step.Defects, d => Assert.Equal(d.Line, d.EndLine));
        Assert.Equal(8, Assert.Single(step.Decoys).Line);
        Assert.Single(step.Hints);
        Assert.True(ReviewCodeEvaluator.Evaluate(step, step.Defects.Select(d => new ReviewFlag(d.Line, d.Category)).ToList()).IsCorrect);
    }

    [Fact]
    public void A_review_step_can_be_a_review_question()
    {
        WritePack(Cache, reviewItems: "  - id: review.sample.cache\n    concept: concept.sample\n    step: step.sample.cache");

        Assert.Empty(Errors());
    }

    [Fact]
    public void The_app_manifest_must_enable_the_step_type()
    {
        WritePack(Cache, stepTypes: "[explain]");

        Assert.Contains(Errors(), e => e.Contains("step type 'review-code' is not enabled"));
    }

    [Fact]
    public void A_diff_can_be_reviewed_and_the_language_is_kept()
    {
        WritePack(Cache.Replace("language: csharp", "language: diff"));

        Assert.Empty(Errors());
    }

    [Fact]
    public void The_language_defaults_to_csharp()
    {
        WritePack(DropLines(Cache, "language: csharp"));

        Assert.Empty(Errors());
    }

    [Fact]
    public void Code_and_defects_are_required()
    {
        WritePack(DropLines(Cache, "code: |"));
        Assert.Contains(Errors(), e => e.Contains("'code' is required"));

        WritePack(Regex.Replace(Cache, @"(?s)    defects:.*?(?=    decoys:)", "    defects: []\n"));
        Assert.Contains(Errors(), e => e.Contains("'defects' is required"));
    }

    [Fact]
    public void A_defect_must_point_inside_the_code()
    {
        WritePack(Cache.Replace("- line: 3", "- line: 11"));

        Assert.Contains(Errors(), e => e.Contains("line 11 is outside the code (it has 10 lines)"));
    }

    [Fact]
    public void A_defect_needs_a_line()
    {
        WritePack(Cache.Replace("      - line: 3\n        category: concurrency", "      - category: concurrency"));

        Assert.Contains(Errors(), e => e.Contains("defects[0] needs the 'line'"));
    }

    [Fact]
    public void A_defect_cannot_end_before_it_starts_or_be_too_long()
    {
        WritePack(Cache.Replace("- line: 3\n", "- line: 3\n        endLine: 2\n"));
        Assert.Contains(Errors(), e => e.Contains("'endLine' 2 is before 'line' 3"));

        WritePack(Cache.Replace("- line: 3\n", "- line: 1\n        endLine: 7\n"));
        Assert.Contains(Errors(), e => e.Contains("spans at most 5 lines"));
    }

    [Fact]
    public void A_defect_on_a_blank_line_is_rejected()
    {
        WritePack(Cache.Replace("      public sealed class Cache\n", "\n      public sealed class Cache\n").Replace("- line: 7", "- line: 1"));

        Assert.Contains(Errors(), e => e.Contains("are blank"));
    }

    [Fact]
    public void The_category_must_be_one_the_learner_can_pick()
    {
        WritePack(Cache.Replace("category: bug", "category: style"));

        Assert.Contains(Errors(), e => e.Contains("'category' must be one of bug, performance, concurrency, api, security (found 'style')"));
    }

    [Fact]
    public void A_defect_needs_an_explanation()
    {
        WritePack(DropLines(Cache, "explanation: A plain Dictionary"));

        Assert.Contains(Errors(), e => e.Contains("defects[0] needs an 'explanation'"));
    }

    [Fact]
    public void Defects_cannot_overlap()
    {
        WritePack(Cache.Replace("- line: 7\n", "- line: 3\n        endLine: 4\n"));

        Assert.Contains(Errors(), e => e.Contains("two defects cover the same line"));
    }

    [Fact]
    public void A_decoy_cannot_sit_on_a_defect_or_repeat()
    {
        WritePack(Cache.Replace("- line: 8", "- line: 7"));
        Assert.Contains(Errors(), e => e.Contains("is part of a defect, so it cannot also be a decoy"));

        WritePack(Cache.Replace("    decoys:\n", "    decoys:\n      - line: 8\n        explanation: Also fine.\n"));
        Assert.Contains(Errors(), e => e.Contains("repeats line 8"));
    }

    [Fact]
    public void A_decoy_needs_an_explanation_and_a_real_line()
    {
        WritePack(DropLines(Cache, "explanation: Calling Load"));
        Assert.Contains(Errors(), e => e.Contains("decoys[0] needs an 'explanation'"));

        WritePack(Cache.Replace("- line: 8", "- line: 40"));
        Assert.Contains(Errors(), e => e.Contains("decoys[0]: line 40 is outside the code"));
    }

    [Fact]
    public void A_review_is_kept_short_enough_for_a_phone()
    {
        var code = string.Join("\n", Enumerable.Range(1, 61).Select(i => $"      int v{i} = {i};"));
        WritePack(Regex.Replace(Cache, @"(?s)    code: \|\n.*?(?=    defects:)", "    code: |\n" + code + "\n"));

        Assert.Contains(Errors(), e => e.Contains("keep a review to at most 60"));
    }

    [Fact]
    public void Question_step_fields_are_refused()
    {
        WritePack(Cache.Replace("    language: csharp\n", "    language: csharp\n    accepted: ['1']\n"));
        Assert.Contains(Errors(), e => e.Contains("a review-code step is answered by flagging lines"));

        WritePack(Cache.Replace("    language: csharp\n", "    language: csharp\n    explanation: Nope.\n"));
        Assert.Contains(Errors(), e => e.Contains("remove 'explanation'"));
    }

    [Fact]
    public void A_review_step_can_prefer_a_large_screen()
    {
        WritePack(Cache.Replace("    language: csharp\n", "    language: csharp\n    largeScreenPreferred: true\n"));

        Assert.Empty(Errors());
    }

    /// <summary>Removes the first line containing each of <paramref name="markers"/>.</summary>
    private static string DropLines(string yaml, params string[] markers)
    {
        var lines = yaml.Split('\n').ToList();
        foreach (var marker in markers) lines.RemoveAt(lines.FindIndex(l => l.Contains(marker, StringComparison.Ordinal)));
        return string.Join('\n', lines);
    }
}
