using Brilliant.Cli;
using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

public sealed class ValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brilliant-tests-" + Guid.NewGuid().ToString("N"));

    public ValidatorTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void WriteValidPack(string lessonYaml = ValidLesson)
    {
        Write("pack.yaml", "id: pack.t\nversion: 1.0.0\n");
        Write("tracks/t/track.yaml", "id: track.t\ntitle: T\n");
        Write("tracks/t/l1/lesson.yaml", lessonYaml);
    }

    private const string ValidLesson = """
        id: lesson.one
        title: One
        steps:
          - id: step.one.intro
            type: explain
            title: Intro
            body: Hello
            comparison:
              csharp: var x = 1;
              python: x = 1
        """;

    [Fact]
    public void Valid_content_passes_and_packs_to_a_loadable_pack()
    {
        WriteValidPack();
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var graph = ContentPackFormat.Load(outPath);
        Assert.Equal("1.0.0", graph.Manifest.Version);
        Assert.Equal(["lesson.one"], graph.Get<Track>("track.t").LessonIds);
        Assert.Equal("track.t", graph.Get<Lesson>("lesson.one").TrackId);
    }

    [Fact]
    public void Missing_directory_is_reported()
    {
        var report = ContentValidator.Validate(Path.Combine(_root, "nope"));
        Assert.Contains(report.Errors, e => e.Message.Contains("does not exist"));
    }

    [Fact]
    public void Bad_id_is_reported_with_file_and_step()
    {
        WriteValidPack(ValidLesson.Replace("step.one.intro", "Step One"));
        var report = ContentValidator.Validate(_root);

        var error = Assert.Single(report.Errors);
        Assert.Equal("tracks/t/l1/lesson.yaml", error.File);
        Assert.Contains("steps[0]", error.Message);
        Assert.Contains("'Step One' is invalid", error.Message);
    }

    [Fact]
    public void Duplicate_ids_across_files_are_reported()
    {
        WriteValidPack();
        Write("tracks/t/l2/lesson.yaml", ValidLesson.Replace("lesson.one", "lesson.two"));

        var report = ContentValidator.Validate(_root);

        Assert.Contains(report.Errors, e => e.Message.Contains("duplicate id 'step.one.intro'"));
    }

    [Fact]
    public void Missing_required_fields_and_incomplete_comparison_are_reported()
    {
        WriteValidPack("""
            id: lesson.one
            title: One
            steps:
              - id: step.one.intro
                type: explain
                title: Intro
                comparison:
                  csharp: var x = 1;
            """);

        var messages = ContentValidator.Validate(_root).Errors.Select(e => e.Message).ToList();

        Assert.Contains(messages, m => m.Contains("'body' is required"));
        Assert.Contains(messages, m => m.Contains("comparison needs 'python'"));
    }

    [Fact]
    public void Unsupported_step_type_is_reported()
    {
        WriteValidPack(ValidLesson.Replace("type: explain", "type: quiz"));
        Assert.Contains(ContentValidator.Validate(_root).Errors, e => e.Message.Contains("unsupported step type 'quiz'"));
    }

    [Fact]
    public void Malformed_yaml_reports_file_and_line()
    {
        WriteValidPack("id: lesson.one\nsteps: [unclosed\n");
        var error = Assert.Single(ContentValidator.Validate(_root).Errors);
        Assert.Equal("tracks/t/l1/lesson.yaml", error.File);
        Assert.Contains("line", error.Message);
    }

    [Fact]
    public void Unknown_field_is_reported_rather_than_silently_ignored()
    {
        WriteValidPack(ValidLesson.Replace("title: Intro", "title: Intro\n    tittle: typo"));
        Assert.NotEmpty(ContentValidator.Validate(_root).Errors);
    }

    [Fact]
    public void Invalid_content_produces_no_pack()
    {
        WriteValidPack(ValidLesson.Replace("step.one.intro", "bad"));
        var outPath = Path.Combine(_root, "p.zip");

        var report = ContentPacker.Pack(_root, outPath);

        Assert.False(report.IsValid);
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void Bad_version_is_reported()
    {
        WriteValidPack();
        Write("pack.yaml", "id: pack.t\nversion: one\n");
        Assert.Contains(ContentValidator.Validate(_root).Errors, e => e.File == "pack.yaml" && e.Message.Contains("version"));
    }

    private static string ChoiceStep(string options, string extra = "") => $"""
        id: lesson.one
        title: One
        steps:
          - id: step.one.q
            type: choice
            title: Q
            prompt: Pick one
            {extra}
            options:
        {Indent(options)}
        """;

    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(l => "    " + l));

    [Fact]
    public void Valid_choice_step_packs_with_options_and_feedback()
    {
        WriteValidPack(ChoiceStep("""
              - text: A
                correct: true
                feedback: because
              - text: B
            """));
        var outPath = Path.Combine(_root, "c.zip");

        Assert.True(ContentPacker.Pack(_root, outPath).IsValid);

        var step = Assert.IsType<ChoiceStep>(ContentPackFormat.Load(outPath).Get<Step>("step.one.q"));
        Assert.False(step.MultiSelect);
        Assert.Equal([true, false], step.Options.Select(o => o.Correct));
        Assert.Equal("because", step.Options[0].Feedback);
    }

    [Fact]
    public void Choice_without_a_correct_option_is_reported()
    {
        WriteValidPack(ChoiceStep("""
              - text: A
              - text: B
            """));
        Assert.Contains(ContentValidator.Validate(_root).Errors, e => e.Message.Contains("no option is marked"));
    }

    [Fact]
    public void Single_choice_with_two_correct_options_requires_multiSelect()
    {
        const string opts = """
              - text: A
                correct: true
              - text: B
                correct: true
            """;
        WriteValidPack(ChoiceStep(opts));
        Assert.Contains(ContentValidator.Validate(_root).Errors, e => e.Message.Contains("multiSelect"));

        WriteValidPack(ChoiceStep(opts, "multiSelect: true"));
        Assert.True(ContentValidator.Validate(_root).IsValid);
    }

    [Fact]
    public void Choice_with_too_few_options_or_missing_text_is_reported()
    {
        WriteValidPack(ChoiceStep("""
              - correct: true
            """));
        var messages = ContentValidator.Validate(_root).Errors.Select(e => e.Message).ToList();
        Assert.Contains(messages, m => m.Contains("at least 2 options"));
        Assert.Contains(messages, m => m.Contains("options[0] needs 'text'"));
    }

    private static string Predict(string body) => "id: lesson.one\ntitle: One\nsteps:\n  - id: step.one.p\n    type: predict-output\n    title: P\n    prompt: What prints?\n    code: print(1)\n" + body;

    private List<string> PredictErrors(string body)
    {
        WriteValidPack(Predict(body));
        return ContentValidator.Validate(_root).Errors.Select(e => e.Message).ToList();
    }

    [Fact]
    public void Valid_typed_predict_step_packs_with_mistakes()
    {
        WriteValidPack(Predict("    accepted: ['1']\n    mistakes:\n      - answers: ['2']\n        feedback: off by one\n      - regex: '^\\d{2}$'\n        feedback: too long\n"));
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<PredictOutputStep>(ContentPackFormat.Load(outPath).Get<Step>("step.one.p"));
        Assert.True(step.IsTyped);
        Assert.Equal("python", step.Code.Language);
        Assert.Equal(2, step.Mistakes.Count);
    }

    [Fact]
    public void Valid_choice_predict_step_is_accepted() =>
        Assert.Empty(PredictErrors("    options:\n      - text: '1'\n        correct: true\n      - text: '2'\n"));

    [Fact]
    public void Predict_needs_exactly_one_variant()
    {
        Assert.Contains(PredictErrors(""), m => m.Contains("either 'accepted'") && m.Contains("neither"));
        Assert.Contains(PredictErrors("    accepted: ['1']\n    options:\n      - text: a\n        correct: true\n      - text: b\n"),
            m => m.Contains("both"));
    }

    [Fact]
    public void Predict_choice_needs_one_correct_option_and_rejects_mistakes()
    {
        Assert.Contains(PredictErrors("    options:\n      - text: a\n      - text: b\n"), m => m.Contains("exactly one option"));
        Assert.Contains(PredictErrors("    options:\n      - text: a\n        correct: true\n      - text: b\n    mistakes:\n      - answers: ['x']\n        feedback: f\n"),
            m => m.Contains("only apply to typed"));
    }

    [Fact]
    public void Bad_mistake_patterns_are_reported()
    {
        var m = PredictErrors("    accepted: ['1']\n    mistakes:\n      - feedback: f\n      - answers: ['2']\n      - answers: [' 1 ']\n        feedback: f\n      - regex: '('\n        feedback: f\n");
        Assert.Contains(m, x => x.Contains("mistakes[0] needs 'answers' and/or 'regex'"));
        Assert.Contains(m, x => x.Contains("mistakes[1] needs 'feedback'"));
        Assert.Contains(m, x => x.Contains("mistakes[2]") && x.Contains("also an accepted answer"));
        Assert.Contains(m, x => x.Contains("mistakes[3]") && x.Contains("invalid regex"));
    }

    private const string LessonHeader = "id: lesson.one\ntitle: One\n";

    private const string QuestionStep = "  - id: step.one.q\n    type: choice\n    title: Q\n    prompt: Pick\n    options:\n      - text: a\n        correct: true\n      - text: b\n";

    private const string ExplainOnly = "  - id: step.one.e\n    type: explain\n    title: E\n    body: b\n";

    private List<string> LessonErrors(string yaml)
    {
        WriteValidPack(yaml);
        return ContentValidator.Validate(_root).Errors.Select(e => e.Message).ToList();
    }

    [Fact]
    public void Concepts_review_items_and_hints_load_into_the_pack()
    {
        WriteValidPack(LessonHeader
            + "concepts:\n  - id: concept.idea\n    title: Idea\n"
            + "reviewItems:\n  - id: review.one.q\n    concept: concept.idea\n    step: step.one.q\n"
            + "steps:\n" + ExplainOnly + QuestionStep + "    hints:\n      - nudge\n      - full answer\n");
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var graph = ContentPackFormat.Load(outPath);
        Assert.Equal("Idea", graph.Get<Concept>("concept.idea").Title);
        var item = graph.Get<ReviewItem>("review.one.q");
        Assert.Equal(("concept.idea", "step.one.q"), (item.ConceptId, item.StepId));
        Assert.Equal(["nudge", "full answer"], graph.Get<Step>("step.one.q").Hints);
        Assert.Empty(graph.Get<Step>("step.one.e").Hints);
        Assert.Single(graph.Get<Lesson>("lesson.one").ReviewItems);
    }

    [Fact]
    public void Review_item_must_point_at_a_declared_concept_and_a_question_step_of_the_lesson()
    {
        var m = LessonErrors(LessonHeader
            + "concepts:\n  - id: concept.idea\n    title: Idea\n"
            + "reviewItems:\n"
            + "  - id: review.one.a\n    concept: concept.missing\n    step: step.one.q\n"
            + "  - id: review.one.b\n    concept: concept.idea\n    step: step.one.nope\n"
            + "  - id: review.one.c\n    concept: concept.idea\n    step: step.one.e\n"
            + "steps:\n" + ExplainOnly + QuestionStep);
        Assert.Contains(m, x => x.Contains("review.one.a") && x.Contains("'concept.missing' is not declared"));
        Assert.Contains(m, x => x.Contains("review.one.b") && x.Contains("not a valid step"));
        Assert.Contains(m, x => x.Contains("review.one.c") && x.Contains("explain step"));
    }

    [Fact]
    public void Concept_and_review_ids_follow_the_id_rules_and_must_be_unique()
    {
        var m = LessonErrors(LessonHeader
            + "concepts:\n  - id: concept.idea\n    title: Idea\n  - id: concept.idea\n    title: Again\n  - id: Idea\n    title: Bad\n  - id: concept.no-title\n"
            + "steps:\n" + QuestionStep);
        Assert.Contains(m, x => x.Contains("duplicate id 'concept.idea'"));
        Assert.Contains(m, x => x.Contains("id 'Idea' is invalid"));
        Assert.Contains(m, x => x.Contains("concept.no-title") && x.Contains("'title' is required"));
    }

    [Fact]
    public void Hints_are_limited_to_questions_and_to_four_levels()
    {
        var onExplain = LessonErrors(LessonHeader + "steps:\n" + ExplainOnly + "    hints:\n      - nudge\n");
        Assert.Contains(onExplain, x => x.Contains("only apply to questions"));

        var tooMany = LessonErrors(LessonHeader + "steps:\n" + QuestionStep + "    hints: [a, b, c, d, e]\n");
        Assert.Contains(tooMany, x => x.Contains("at most 4 hints"));

        var blank = LessonErrors(LessonHeader + "steps:\n" + QuestionStep + "    hints: ['a', ' ']\n");
        Assert.Contains(blank, x => x.Contains("hints[1] is empty"));
    }

    [Fact]
    public void Shipped_content_is_valid_and_every_lesson_is_fully_authored()
    {
        var root = FindRepoContent();
        var report = ContentValidator.Load(root, out var content);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        Assert.NotNull(content);
        Assert.True(content!.Lessons.Count >= 2);
        foreach (var lesson in content.Lessons)
        {
            Assert.True(lesson.Steps.Count >= 10, $"{lesson.Id} has too few steps for a 15-20 minute lesson.");
            Assert.NotEmpty(lesson.Concepts);
            Assert.NotEmpty(lesson.ReviewItems);
            Assert.Contains(lesson.Steps, s => s is PredictOutputStep);
            Assert.Contains(lesson.Steps, s => s is ChoiceStep);
            // Every question carries a hint ladder.
            Assert.All(lesson.Steps.Where(s => s is not ExplainStep), s => Assert.NotEmpty(s.Hints));
            // Every declared concept is exercised by at least one review item.
            Assert.All(lesson.Concepts, c => Assert.Contains(lesson.ReviewItems, r => r.ConceptId == c.Id));
        }
    }

    private static string FindRepoContent()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "content", "pack.yaml")))
                return Path.Combine(dir.FullName, "content");
        throw new DirectoryNotFoundException("Could not find the repo's content directory.");
    }
}
