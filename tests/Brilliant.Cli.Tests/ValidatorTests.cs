using Brilliant.Cli;
using Brilliant.Core.Content;
using Brilliant.Core.Python;

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
    public void Review_item_kind_is_concept_unless_the_author_says_pattern_and_travels_in_the_pack()
    {
        WriteValidPack(LessonHeader
            + "concepts:\n  - id: concept.idea\n    title: Idea\n"
            + "reviewItems:\n"
            + "  - id: review.one.plain\n    concept: concept.idea\n    step: step.one.q\n"
            + "  - id: review.one.concept\n    concept: concept.idea\n    step: step.one.q\n    kind: concept\n"
            + "  - id: review.one.pattern\n    concept: concept.idea\n    step: step.one.q\n    kind: pattern\n"
            + "steps:\n" + ExplainOnly + QuestionStep);
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var graph = ContentPackFormat.Load(outPath);
        Assert.Equal(ReviewKind.Concept, graph.Get<ReviewItem>("review.one.plain").Kind);
        Assert.Equal(ReviewKind.Concept, graph.Get<ReviewItem>("review.one.concept").Kind);
        Assert.Equal(ReviewKind.Pattern, graph.Get<ReviewItem>("review.one.pattern").Kind);
    }

    [Theory]
    [InlineData("resolve")]
    [InlineData("Pattern")]
    [InlineData("trivia")]
    public void Review_item_kind_must_be_concept_or_pattern(string kind)
    {
        var m = LessonErrors(LessonHeader
            + "concepts:\n  - id: concept.idea\n    title: Idea\n"
            + $"reviewItems:\n  - id: review.one.a\n    concept: concept.idea\n    step: step.one.q\n    kind: {kind}\n"
            + "steps:\n" + ExplainOnly + QuestionStep);
        Assert.Contains(m, x => x.Contains("review.one.a") && x.Contains($"kind '{kind}' is not one of: concept, pattern"));
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

    [Theory]
    [InlineData("concepts", "concepts:\n  -\n")]
    [InlineData("concepts", "concepts:\n  - ~\n")]
    [InlineData("reviewItems", "reviewItems:\n  -\n")]
    [InlineData("reviewItems", "reviewItems:\n  - null\n")]
    public void Null_declaration_entries_are_reported_not_crashed_on(string list, string declaration)
    {
        var m = LessonErrors(LessonHeader + declaration + "steps:\n" + QuestionStep);
        Assert.Contains(m, x => x.Contains($"{list}[0]") && x.Contains("entry is empty"));
    }

    [Theory]
    [InlineData("steps[0]", "steps:\n  -\n" + QuestionStep)]
    [InlineData("options[0]", "steps:\n  - id: step.one.q\n    type: choice\n    title: Q\n    prompt: Pick\n    options:\n      -\n      - text: a\n        correct: true\n      - text: b\n")]
    [InlineData("snippets[0]", "steps:\n  - id: step.one.e\n    type: explain\n    title: E\n    body: b\n    snippets:\n      -\n")]
    public void Null_entries_inside_steps_are_reported_not_crashed_on(string where, string steps)
    {
        var m = LessonErrors(LessonHeader + steps);
        Assert.Contains(m, x => x.Contains(where) && x.Contains("entry is empty"));
    }

    [Fact]
    public void Null_entries_in_predict_output_options_and_mistakes_are_reported()
    {
        var typed = LessonErrors(Predict("    accepted: ['1']\n    mistakes:\n      -\n"));
        Assert.Contains(typed, x => x.Contains("mistakes[0]") && x.Contains("entry is empty"));

        var choice = LessonErrors(Predict("    options:\n      -\n      - text: a\n        correct: true\n      - text: b\n"));
        Assert.Contains(choice, x => x.Contains("options[0]") && x.Contains("entry is empty"));
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

    private const string WriteCodeHead = "  - id: step.one.w\n    type: write-code\n    title: W\n    prompt: Write it\n";
    private const string AddSolution = "    solution: |\n      def add(a, b):\n          return a + b\n";
    private const string WriteCodeBody = "    language: python\n    entrypoint: add\n    starter: |\n      def add(a, b):\n          pass\n" + AddSolution + "    tests:\n      - input: \"1, 2\"\n        expected: \"3\"\n";

    [Fact]
    public void Write_code_step_loads_into_the_pack()
    {
        WriteValidPack(LessonHeader + "steps:\n" + WriteCodeHead + WriteCodeBody
            + "      - input: \"\"\n        expected: \"0\"\n    hints:\n      - nudge\n");
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath, new FakeRunner(_ => Outcome(TestRunStatus.Passed)));

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<WriteCodeStep>(ContentPackFormat.Load(outPath).Get<Step>("step.one.w"));
        Assert.Equal(("python", "add"), (step.Language, step.Entrypoint));
        Assert.Equal("def add(a, b):\n    pass\n", step.Starter);
        Assert.Equal([new CodeTest("1, 2", "3"), new CodeTest("", "0")], step.Tests);
        Assert.Equal(["nudge"], step.Hints);
    }

    [Fact]
    public void Write_code_step_requires_prompt_entrypoint_language_and_tests()
    {
        var m = LessonErrors(LessonHeader + "steps:\n  - id: step.one.w\n    type: write-code\n    title: W\n");
        Assert.Contains(m, x => x.Contains("'prompt' is required"));
        Assert.Contains(m, x => x.Contains("'entrypoint' is required"));
        Assert.Contains(m, x => x.Contains("'language' is required"));
        Assert.Contains(m, x => x.Contains("'solution' is required"));
        Assert.Contains(m, x => x.Contains("at least one hidden test"));
    }

    [Fact]
    public void Write_code_language_and_entrypoint_are_checked()
    {
        var m = LessonErrors(LessonHeader + "steps:\n" + WriteCodeHead
            + "    language: java\n    entrypoint: 2fast\n    tests:\n      - input: '1'\n        expected: '1'\n");
        Assert.Contains(m, x => x.Contains("unsupported language 'java'"));
        Assert.Contains(m, x => x.Contains("'2fast' is not a valid Python function name"));
    }

    [Fact]
    public void Write_code_tests_need_input_and_expected()
    {
        var m = LessonErrors(LessonHeader + "steps:\n" + WriteCodeHead
            + "    language: python\n    entrypoint: add\n    tests:\n      - expected: '1'\n      - input: '1'\n      -\n");
        Assert.Contains(m, x => x.Contains("tests[0] needs 'input'"));
        Assert.Contains(m, x => x.Contains("tests[1] needs 'expected'"));
        Assert.Contains(m, x => x.Contains("tests[2]") && x.Contains("entry is empty"));
    }

    private const string FillHead = "  - id: step.one.f\n    type: fill-blank\n    title: F\n    prompt: Fill it in\n";
    private const string FillBody = "    template: |\n      for i in {{iter}}:\n          total {{op}} i\n    blanks:\n      - id: iter\n        accepted: [range(n)]\n      - id: op\n        accepted: [\"+=\"]\n";

    [Fact]
    public void Fill_blank_step_loads_into_the_pack()
    {
        WriteValidPack(LessonHeader + "steps:\n" + FillHead + FillBody
            + "        mistakes:\n          - answers: [\"=\"]\n            feedback: Plain = replaces the total.\n    hints:\n      - nudge\n");
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<FillBlankStep>(ContentPackFormat.Load(outPath).Get<Step>("step.one.f"));
        Assert.Equal("python", step.Language);
        Assert.Equal("for i in {{iter}}:\n    total {{op}} i\n", step.Template);
        Assert.Equal(["iter", "op"], step.Blanks.Select(b => b.Id));
        Assert.Equal(["range(n)"], step.Blanks[0].Accepted);
        Assert.Equal("Plain = replaces the total.", step.Blanks[1].Mistakes.Single().Feedback);
        Assert.Equal(["nudge"], step.Hints);
    }

    [Fact]
    public void Fill_blank_requires_prompt_template_and_blanks()
    {
        var m = LessonErrors(LessonHeader + "steps:\n  - id: step.one.f\n    type: fill-blank\n    title: F\n");
        Assert.Contains(m, x => x.Contains("'prompt' is required"));
        Assert.Contains(m, x => x.Contains("'template' is required"));
        Assert.Contains(m, x => x.Contains("at least one entry in 'blanks'"));
    }

    [Fact]
    public void A_blank_without_accepted_answers_is_rejected()
    {
        var none = LessonErrors(LessonHeader + "steps:\n" + FillHead + "    template: x = {{a}}\n    blanks:\n      - id: a\n");
        Assert.Contains(none, x => x.Contains("'a'") && x.Contains("needs at least one accepted answer"));

        var empty = LessonErrors(LessonHeader + "steps:\n" + FillHead + "    template: x = {{a}}\n    blanks:\n      - id: a\n        accepted: ['  ']\n");
        Assert.Contains(empty, x => x.Contains("accepted[0] is empty"));
    }

    [Fact]
    public void Accepted_answers_must_be_single_line_and_distinct()
    {
        var m = LessonErrors(LessonHeader + "steps:\n" + FillHead
            + "    template: x = {{a}}\n    blanks:\n      - id: a\n        accepted: [\"1\\n2\", \"7\", \" 7 \"]\n");
        Assert.Contains(m, x => x.Contains("accepted[0] spans several lines"));
        Assert.Contains(m, x => x.Contains("accepted[2] repeats another accepted answer"));
    }

    [Fact]
    public void Template_markers_and_declared_blanks_must_match_exactly()
    {
        var m = LessonErrors(LessonHeader + "steps:\n" + FillHead
            + "    template: \"x = {{a}} + {{a}} + {{ghost}} + {{Bad Id}}\"\n    blanks:\n      - id: a\n        accepted: ['1']\n      - id: unused\n        accepted: ['2']\n");
        Assert.Contains(m, x => x.Contains("blank 'a' appears more than once"));
        Assert.Contains(m, x => x.Contains("uses blank 'ghost' which is not declared"));
        Assert.Contains(m, x => x.Contains("'{{Bad Id}}' is not a valid blank"));
        Assert.Contains(m, x => x.Contains("blank 'unused' is declared but never used"));
    }

    [Fact]
    public void Blank_ids_must_be_valid_and_unique()
    {
        var m = LessonErrors(LessonHeader + "steps:\n" + FillHead
            + "    template: x = {{a}}\n    blanks:\n      - id: a\n        accepted: ['1']\n      - id: a\n        accepted: ['2']\n      - id: Bad_Id\n        accepted: ['3']\n      - accepted: ['4']\n");
        Assert.Contains(m, x => x.Contains("duplicate blank id 'a'"));
        Assert.Contains(m, x => x.Contains("id 'Bad_Id' is invalid"));
        Assert.Contains(m, x => x.Contains("blanks[3]") && x.Contains("'id' is required"));
    }

    [Fact]
    public void Blank_mistakes_follow_the_same_rules_as_typed_predictions()
    {
        var m = LessonErrors(LessonHeader + "steps:\n" + FillHead
            + "    template: x = {{a}}\n    blanks:\n      - id: a\n        accepted: ['1']\n        mistakes:\n          - answers: ['1']\n            feedback: dup\n          - regex: '['\n            feedback: bad\n          - answers: ['2']\n");
        Assert.Contains(m, x => x.Contains("mistakes[0]") && x.Contains("also an accepted answer"));
        Assert.Contains(m, x => x.Contains("mistakes[1]") && x.Contains("invalid regex"));
        Assert.Contains(m, x => x.Contains("mistakes[2] needs 'feedback'"));
    }

    [Fact]
    public void Fill_blank_language_is_checked_and_defaults_to_python()
    {
        var m = LessonErrors(LessonHeader + "steps:\n" + FillHead + FillBody + "    language: java\n");
        Assert.Contains(m, x => x.Contains("unsupported language 'java'"));
    }

    [Fact]
    public void A_fill_blank_step_can_back_a_review_item()
    {
        var yaml = LessonHeader + "concepts:\n  - id: concept.loops\n    title: Loops\nreviewItems:\n  - id: review.one.loop\n    concept: concept.loops\n    step: step.one.f\nsteps:\n" + FillHead + FillBody;
        Assert.Empty(LessonErrors(yaml));
    }

    private sealed class FakeRunner(Func<string, TestRunResult> result) : IReferenceSolutionRunner
    {
        public List<string> Solutions { get; } = [];
        public TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests) { Solutions.Add(code); return result(code); }
    }

    private static TestRunResult Outcome(TestRunStatus status, params TestOutcome[] tests) =>
        new(status, "", status == TestRunStatus.Error ? "Your code raised an error before the tests could run." : null,
            status == TestRunStatus.Error ? "NameError: name 'x' is not defined" : null, null, tests);

    private void WriteAddLesson(string solution = AddSolution) =>
        WriteValidPack(LessonHeader + "steps:\n" + WriteCodeHead
            + "    language: python\n    entrypoint: add\n" + solution + "    tests:\n      - input: '1, 2'\n        expected: '3'\n");

    [Fact]
    public void Reference_solutions_are_run_but_never_shipped_in_the_pack()
    {
        WriteAddLesson();
        var runner = new FakeRunner(_ => Outcome(TestRunStatus.Passed));
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath, runner);

        Assert.True(report.IsValid);
        Assert.Single(runner.Solutions);
        Assert.Contains("return a + b", runner.Solutions[0]);
        using var zip = System.IO.Compression.ZipFile.OpenRead(outPath);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain("return a + b", reader.ReadToEnd());
        }
    }

    [Fact]
    public void A_failing_reference_solution_is_reported_with_step_test_expected_and_actual_and_blocks_packing()
    {
        WriteAddLesson();
        var runner = new FakeRunner(_ => Outcome(TestRunStatus.Failed,
            new TestOutcome("add(1, 2)", "3", "-1", false, "", null, null)));
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath, runner);

        Assert.False(report.IsValid);
        Assert.False(File.Exists(outPath));
        var error = Assert.Single(report.Errors);
        Assert.Contains("lesson.yaml", error.File);
        Assert.Contains("step.one.w", error.Message);
        Assert.Contains("test 1: add(1, 2)", error.Message);
        Assert.Contains("expected: 3", error.Message);
        Assert.Contains("actual:   -1", error.Message);
    }

    [Fact]
    public void A_reference_solution_that_cannot_run_is_reported_with_its_error()
    {
        WriteAddLesson();
        var report = ContentValidator.Validate(_root, new FakeRunner(_ => Outcome(TestRunStatus.Error)));
        Assert.Contains(report.Errors, e => e.Message.Contains("reference solution is wrong") && e.Message.Contains("NameError"));
    }

    [Fact]
    public void Solutions_are_not_run_when_the_content_is_otherwise_invalid()
    {
        WriteAddLesson();
        Write("tracks/t/track.yaml", "id: bad id\ntitle: T\n");
        var runner = new FakeRunner(_ => Outcome(TestRunStatus.Passed));

        Assert.False(ContentValidator.Validate(_root, runner).IsValid);
        Assert.Empty(runner.Solutions);
    }

    [Fact]
    public void A_missing_python_is_reported_once_with_how_to_fix_it()
    {
        WriteAddLesson();
        var report = ContentValidator.Validate(_root, new CPythonRunner(python: "definitely-not-a-python"));
        var error = Assert.Single(report.Errors);
        Assert.Contains("BRILLIANT_PYTHON", error.Message);
    }

    [Fact]
    public void Real_cpython_accepts_a_correct_solution_and_rejects_a_wrong_one()
    {
        var runner = new CPythonRunner();
        WriteAddLesson();
        Assert.True(ContentValidator.Validate(_root, runner).IsValid);

        WriteAddLesson("    solution: |\n      def add(a, b):\n          return a - b\n");
        var wrong = Assert.Single(ContentValidator.Validate(_root, runner).Errors);
        Assert.Contains("add(1, 2)", wrong.Message);
        Assert.Contains("expected: 3", wrong.Message);
        Assert.Contains("actual:   -1", wrong.Message);
    }

    [Fact]
    public void Real_cpython_stops_a_solution_that_never_finishes()
    {
        WriteAddLesson("    solution: |\n      def add(a, b):\n          while True:\n              pass\n");
        var report = ContentValidator.Validate(_root, new CPythonRunner(TimeSpan.FromSeconds(2)));
        Assert.Contains(report.Errors, e => e.Message.Contains("ran for more than 2 seconds"));
    }

    [Fact]
    public void Write_code_step_rejects_unknown_fields()
    {
        var m = LessonErrors(LessonHeader + "steps:\n" + WriteCodeHead + WriteCodeBody + "    bogus: 1\n");
        Assert.NotEmpty(m); // unknown YAML fields are validation errors, as for every other step type
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

    private const string ParsonsHead = "  - id: step.one.p\n    type: parsons\n    title: P\n    prompt: Put these in order\n";

    private static string Solution(params string[] lines) =>
        "    solution: |\n" + string.Concat(lines.Select(l => "      " + l + "\n"));

    private List<string> ParsonsErrors(string body) => LessonErrors(LessonHeader + "steps:\n" + ParsonsHead + body);

    [Fact]
    public void Parsons_step_loads_into_the_pack_with_levels_worked_out_from_indentation()
    {
        WriteValidPack(LessonHeader + "steps:\n" + ParsonsHead
            + Solution("def grade(score):", "    if score >= 50:", "        return 'pass'", "    else:", "        return 'fail'")
            + "    hints:\n      - nudge\n");
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<ParsonsStep>(ContentPackFormat.Load(outPath).Get<Step>("step.one.p"));
        Assert.Equal("python", step.Language);
        Assert.Equal(["def grade(score):", "if score >= 50:", "return 'pass'", "else:", "return 'fail'"], step.Lines.Select(l => l.Text));
        Assert.Equal([0, 1, 2, 1, 2], step.Lines.Select(l => l.Level));
        Assert.Equal(["nudge"], step.Hints);
    }

    [Fact]
    public void Parsons_requires_prompt_and_solution()
    {
        var m = LessonErrors(LessonHeader + "steps:\n  - id: step.one.p\n    type: parsons\n    title: P\n");
        Assert.Contains(m, x => x.Contains("'prompt' is required"));
        Assert.Contains(m, x => x.Contains("'solution' is required"));
    }

    [Fact]
    public void Parsons_needs_at_least_two_lines_and_two_distinct_ones()
    {
        Assert.Contains(ParsonsErrors(Solution("print(1)")), x => x.Contains("at least 2 lines"));
        Assert.Contains(ParsonsErrors(Solution("print(1)", "print(1)")), x => x.Contains("only one distinct line"));
    }

    [Fact]
    public void Parsons_rejects_tabs_and_uneven_indentation()
    {
        Assert.Contains(ParsonsErrors("    solution: \"if x:\\n\\tpass\"\n"), x => x.Contains("uses a tab"));
        Assert.Contains(ParsonsErrors(Solution("if x:", "    if y:", "       pass")), x => x.Contains("line 3 is indented 7 spaces") && x.Contains("(4)"));
    }

    [Fact]
    public void Parsons_checks_that_indentation_follows_the_colons()
    {
        // A block scalar takes its indentation from its first line, so an indented first line needs an explicit indicator (|2).
        Assert.Contains(ParsonsErrors("    solution: |2\n          x = 1\n      y = 2\n"), x => x.Contains("first line must not be"));
        Assert.Contains(ParsonsErrors(Solution("if x:", "y = 2")), x => x.Contains("'solution' line 1 ends with ':' so line 2 must be indented one level deeper"));
        Assert.Contains(ParsonsErrors(Solution("x = 1", "    y = 2")), x => x.Contains("indented deeper than the line before it, which doesn't end with ':'"));
        Assert.Contains(ParsonsErrors(Solution("x = 1", "if x:")), x => x.Contains("ends with ':' on line 2 but nothing follows it"));
    }

    [Fact]
    public void Parsons_block_headers_may_carry_a_trailing_comment()
    {
        var valid = ParsonsErrors(Solution("def grade(score):  # pass mark is 50", "    if score >= 50:  # the pass case", "        return 'pass'", "    return 'fail'"));
        Assert.Empty(valid);

        // The '#' in a string isn't a comment, and a ':' inside a comment doesn't open a block.
        Assert.Empty(ParsonsErrors(Solution("if x == '#':", "    y = 1")));
        Assert.Empty(ParsonsErrors(Solution("x = 1  # note:", "y = 2")));
        Assert.Contains(ParsonsErrors(Solution("if x:  # note", "y = 2")), e => e.Contains("line 1 ends with ':' so line 2 must be indented one level deeper"));
    }

    [Fact]
    public void Parsons_language_must_be_python()
    {
        var m = ParsonsErrors("    language: ruby\n" + Solution("x = 1", "y = 2"));
        Assert.Contains(m, x => x.Contains("unsupported language 'ruby'"));
    }

    [Fact]
    public void Parsons_hints_are_allowed_on_a_parsons_step()
    {
        WriteValidPack(LessonHeader + "steps:\n" + ParsonsHead + Solution("x = 1", "y = 2") + "    hints:\n      - a\n");
        Assert.True(ContentValidator.Validate(_root).IsValid);
    }

    private static string FindRepoContent()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "content", "pack.yaml")))
                return Path.Combine(dir.FullName, "content");
        throw new DirectoryNotFoundException("Could not find the repo's content directory.");
    }
}
