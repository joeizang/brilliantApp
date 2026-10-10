using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Drafts;
using Brilliant.Core.Python;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>What the Review Queue Builder changes on screen: the daily cap, pattern and re-solve items.</summary>
public class ReviewQueueViewTests : ShortcutContext
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private sealed class MemoryDrafts : ICodeDraftStore
    {
        public Dictionary<string, string> Saved { get; } = [];
        public string? Get(string stepId) => Saved.GetValueOrDefault(stepId);
        public void Save(string stepId, string code) => Saved[stepId] = code;
        public void Reset(string stepId) => Saved.Remove(stepId);
    }

    private sealed class FakeRuntime : IPythonRuntime
    {
        public Task<TestRunResult> RunTestsAsync(string code, string entrypoint, IReadOnlyList<CodeTest> tests, CancellationToken ct = default) =>
            Task.FromResult(new TestRunResult(TestRunStatus.Passed, "", null, null, null, []));
        public Task<TraceResult> TraceAsync(string code, IReadOnlyList<Visual> visuals, CancellationToken ct = default) =>
            throw new NotSupportedException("This test does not trace.");
    }

    private static ChoiceStep Q(string id) =>
        new(id, "Question " + id, "Pick the right one", false, [new ChoiceOption("right", true, null), new ChoiceOption("wrong", false, null)]);

    private static readonly FillBlankStep Fill = new("step.fill", "Fill the blank", "Complete it", "python", "x = {{v}}", [new Blank("v", ["1"], [])]);

    private static Lesson MakeLesson(int questions) => new("lesson.l", "Lesson", "track.t",
        [new ExplainStep("step.intro", "Intro", "b", [], null), .. Enumerable.Range(1, questions).Select(i => Q($"step.q{i}")), Fill])
    {
        Concepts = [new Concept("concept.c", "C")],
        ReviewItems = [.. Enumerable.Range(1, questions).Select(i => new ReviewItem($"review.q{i}", "concept.c", $"step.q{i}", i == 1 ? ReviewKind.Pattern : ReviewKind.Concept))],
    };

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private readonly ProgressRecorder _recorder;

    public ReviewQueueViewTests()
    {
        _recorder = new ProgressRecorder(_log, "device", _clock);
        Services.AddSingleton(_recorder);
    }

    private static ContentGraph Graph(Lesson lesson) => new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [lesson.Id])], [lesson]);

    private IRenderedComponent<ReviewView> Open(Lesson lesson) => Render<ReviewView>(p => p.Add(c => c.Content, Graph(lesson)));

    private void Finish(Lesson lesson)
    {
        foreach (var s in lesson.Steps) _recorder.CompleteStep(lesson, s.Id);
    }

    private IEnumerable<ProgressEvent> Answers => _log.Items.Where(e => e.Type == ProgressEventTypes.ReviewAnswered);

    [Fact]
    public void The_queue_stops_at_the_daily_cap()
    {
        var lesson = MakeLesson(25);
        Finish(lesson);

        var cut = Open(lesson);

        Assert.Contains("1 of 20", cut.Find(".review-progress").TextContent);
    }

    [Fact]
    public void When_todays_cap_is_used_up_the_screen_says_more_are_waiting_for_tomorrow()
    {
        var lesson = MakeLesson(22);
        Finish(lesson);
        // Twenty answered earlier today (each now scheduled days ahead), two untouched and still due.
        foreach (var item in lesson.ReviewItems.Take(20))
            _recorder.ReviewAnswered(lesson.Id, item, true, 0, TimeSpan.FromSeconds(10), Rating.Good);
        _clock.Now = T0.AddHours(3);

        var cut = Open(lesson);

        Assert.Contains("all for today", cut.Find(".review-done").TextContent);
        Assert.Contains("2 more are waiting", cut.Find(".review-next").TextContent);
        Assert.Empty(cut.FindAll("ul.options"));
    }

    [Fact]
    public void Tomorrow_the_waiting_items_are_offered()
    {
        var lesson = MakeLesson(22);
        Finish(lesson);
        foreach (var item in lesson.ReviewItems.Take(20))
            _recorder.ReviewAnswered(lesson.Id, item, true, 0, TimeSpan.FromSeconds(10), Rating.Good);
        _clock.Now = T0.AddDays(1);

        var cut = Open(lesson);

        Assert.Contains("1 of 2", cut.Find(".review-progress").TextContent);
    }

    [Fact]
    public void A_pattern_item_is_labelled_and_comes_first()
    {
        var lesson = MakeLesson(3) with { ReviewItems = MakeLesson(3).ReviewItems.Reverse().ToList() };
        Finish(lesson);

        var cut = Open(lesson);

        Assert.Equal("Pattern", cut.Find(".review-kind").TextContent.Trim());
        Assert.Contains("Question step.q1", cut.Markup);
    }

    [Fact]
    public void Ordinary_concept_items_have_no_label()
    {
        var lesson = MakeLesson(3) with { ReviewItems = [new ReviewItem("review.q2", "concept.c", "step.q2")] };
        Finish(lesson);

        Assert.Empty(Open(lesson).FindAll(".review-kind"));
    }

    [Fact]
    public void A_problem_answered_wrongly_comes_back_as_a_labelled_re_solve_that_can_be_answered_here()
    {
        var lesson = MakeLesson(1) with { ReviewItems = [] };
        _recorder.StepAnswered(lesson.Id, Fill.Id, false, new Dictionary<string, string> { ["v"] = "2" });
        Finish(lesson);

        var cut = Open(lesson);

        Assert.Equal("Re-solve", cut.Find(".review-kind").TextContent.Trim());
        Assert.Contains("Fill the blank", cut.Markup);
        cut.Find("input.blank").Input("1");
        cut.Find("button.primary").Click();

        var answer = Assert.Single(Answers);
        var data = JsonDocument.Parse(answer.Data!).RootElement;
        Assert.Equal(("resolve.step.fill", true), (data.GetProperty("item").GetString(), data.GetProperty("correct").GetBoolean()));
        Assert.Equal(Fill.Id, answer.StepId);
        Assert.Contains("You'll see this again", cut.Find(".review-next").TextContent);
    }

    [Fact]
    public void A_re_solve_is_not_offered_again_straight_after_it_is_answered()
    {
        var lesson = MakeLesson(1) with { ReviewItems = [] };
        _recorder.StepAnswered(lesson.Id, Fill.Id, false, new Dictionary<string, string> { ["v"] = "2" });
        Finish(lesson);
        var cut = Open(lesson);
        cut.Find("input.blank").Input("1");
        cut.Find("button.primary").Click();

        Assert.Empty(Open(lesson).FindAll("input.blank"));
        Assert.Contains("all caught up", Open(lesson).Find(".review-done").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // --- Write-code re-solves must start from the starter, not from the learner's saved lesson solution ----------------

    private static readonly WriteCodeStep Write = new("step.write", "Echo", "Write f", "python", "def f(x):\n    pass\n", "f", [new CodeTest("7", "7")]);
    private const string LessonSolution = "def f(x):\n    return x\n";

    private (IRenderedComponent<ReviewView> Cut, BunitJSModuleInterop Module, MemoryDrafts Drafts, Lesson Lesson) OpenWriteCodeResolve(Action<MemoryDrafts>? arrange = null)
    {
        var lesson = new Lesson("lesson.w", "Write", "track.t", [new ExplainStep("step.intro", "Intro", "b", [], null), Write]);
        _recorder.CodeSubmitted(lesson.Id, Write.Id, "def f(x): pass", 0, 1);        // got it wrong first,
        _recorder.CodeSubmitted(lesson.Id, Write.Id, LessonSolution, 1, 1);          // then solved it to finish the lesson
        Finish(lesson);
        var drafts = new MemoryDrafts { Saved = { [Write.Id] = LessonSolution } };    // and the editor autosaved that solution
        arrange?.Invoke(drafts);

        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/code-editor.js");
        module.Setup<int>("create", _ => true).SetResult(1);
        module.Setup<string>("getCode", _ => true).SetResult("def f(x):\n    return x + 1\n");
        Services.AddSingleton<ICodeDraftStore>(drafts);
        Services.AddSingleton<IPythonRuntime>(new FakeRuntime());

        var cut = Open(lesson);
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));
        return (cut, module, drafts, lesson);
    }

    private static string EditorStartedWith(BunitJSModuleInterop module) => (string)module.Invocations["create"].Single().Arguments[1]!;

    [Fact]
    public void A_write_code_re_solve_starts_from_the_starter_not_the_saved_lesson_solution()
    {
        var (_, module, drafts, _) = OpenWriteCodeResolve();

        Assert.Equal(Write.Starter, EditorStartedWith(module));
        Assert.Equal(LessonSolution, drafts.Saved[Write.Id]);   // the lesson's own draft is left alone
    }

    [Fact]
    public void Typing_in_a_re_solve_never_overwrites_the_lessons_draft()
    {
        var (_, module, drafts, _) = OpenWriteCodeResolve();

        ((DotNetObjectReference<CodeEditor>)module.Invocations["create"].Single().Arguments[2]!).Value.OnJsChanged("def f(x):\n    return 1\n");

        Assert.Equal(LessonSolution, drafts.Saved[Write.Id]);
        Assert.Equal("def f(x):\n    return 1\n", drafts.Saved["review.resolve.step.write.0"]);
    }

    [Fact]
    public void An_interrupted_re_solve_resumes_where_it_was_left()
    {
        var (_, module, _, _) = OpenWriteCodeResolve(d => d.Saved["review.resolve.step.write.0"] = "def f(x):\n    return 2\n");

        Assert.Equal("def f(x):\n    return 2\n", EditorStartedWith(module));
    }

    [Fact]
    public void The_next_due_attempt_starts_fresh_again_after_one_was_answered()
    {
        var (cut, module, drafts, lesson) = OpenWriteCodeResolve();
        cut.Find("button.primary").Click();                                           // Run tests: the first check is recorded
        cut.WaitForState(() => Answers.Any());
        var due = _recorder.Project(Graph(lesson)).Reviews.Single(r => r.Item.Id == "resolve.step.write").Card!.Due;
        drafts.Saved["review.resolve.step.write.0"] = "def f(x):\n    return x + 1\n";

        _clock.Now = due;
        var second = Open(lesson);
        second.WaitForAssertion(() => Assert.Equal(2, module.Invocations["create"].Count));

        Assert.Equal(Write.Starter, (string)module.Invocations["create"][1].Arguments[1]!);
    }
}
