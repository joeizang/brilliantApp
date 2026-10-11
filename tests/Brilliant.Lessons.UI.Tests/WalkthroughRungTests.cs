using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Python;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The hint ladder's last rung: the reference solution played in the Trace Player, with why it works.</summary>
public class WalkthroughRungTests : ShortcutContext
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private static readonly Walkthrough Solution = new("def f(n):\n    return n\n\nresult = f(2)\n",
        [new Visual("nums", Visual.Array, [])], "It **returns** what it is given.");

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private sealed class FakeRuntime : IPythonRuntime
    {
        public List<(string Code, IReadOnlyList<Visual> Visuals)> Traced { get; } = [];

        public Task<TestRunResult> RunTestsAsync(string code, string entrypoint, IReadOnlyList<CodeTest> tests, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TraceResult> TraceAsync(string code, IReadOnlyList<Visual> visuals, CancellationToken ct = default)
        {
            Traced.Add((code, visuals));
            var frames = new[] { 1, 2 }.Select(i => new TraceFrame("line", i, "<module>", [new("n", "int", i.ToString())],
                new Dictionary<string, TrackedArray> { ["nums"] = new("list", ["1"], 0) })).ToList();
            return Task.FromResult(new TraceResult(TraceStatus.Ok, frames, "", null, null, null));
        }
    }

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private readonly ProgressRecorder _recorder;
    private readonly FakeRuntime _runtime = new();

    public WalkthroughRungTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/code-editor.js").Setup<int>("create", _ => true).SetResult(1);
        Services.AddSingleton<IPythonRuntime>(_runtime);
        _recorder = new ProgressRecorder(_log, "device", _clock);
        Services.AddSingleton(_recorder);
    }

    private IRenderedComponent<HintLadder> Ladder(IReadOnlyList<string> hints, List<int>? levels = null, Walkthrough? walkthrough = null, int initial = 0) =>
        Render<HintLadder>(p => p.Add(c => c.Hints, hints).Add(c => c.Walkthrough, walkthrough ?? Solution)
            .Add(c => c.Initial, initial).Add(c => c.OnRevealed, l => levels?.Add(l)));

    private static string Button(IRenderedComponent<HintLadder> cut) => cut.Find("button.hint-more").TextContent.Trim();

    private static string[] Labels(IRenderedComponent<HintLadder> cut) => cut.FindAll(".hint-label").Select(l => l.TextContent.Trim()).ToArray();

    [Fact]
    public void The_walkthrough_is_the_last_rung_after_the_hints_and_is_asked_for_by_name()
    {
        var cut = Ladder(["nudge", "pattern", "partial"]);

        cut.Find("button.hint-more").Click();
        cut.Find("button.hint-more").Click();
        Assert.Equal("Show another hint", Button(cut));   // the third hint is next, not the solution
        cut.Find("button.hint-more").Click();

        Assert.Equal("Show the full solution", Button(cut));
        Assert.Empty(cut.FindAll(".walkthrough"));
        Assert.Empty(_runtime.Traced);   // nothing runs until it is asked for

        cut.Find("button.hint-more").Click();

        Assert.Equal(["Nudge", "Pattern hint", "Partial code", "Full solution"], Labels(cut));
        Assert.Empty(cut.FindAll("button.hint-more"));
    }

    [Fact]
    public void The_rung_shows_the_explanation_beside_the_solution_playing_in_the_trace_player()
    {
        var cut = Ladder([]);

        cut.Find("button.hint-more").Click();

        var rung = cut.Find(".walkthrough");
        Assert.Contains("<strong>returns</strong>", rung.QuerySelector(".walkthrough-explanation")!.InnerHtml);
        cut.WaitForAssertion(() => Assert.NotNull(rung.QuerySelector(".trace-player")));
        var traced = Assert.Single(_runtime.Traced);
        Assert.Equal(Solution.Code, traced.Code);
        Assert.Equal(Solution.Visuals, traced.Visuals);
    }

    [Fact]
    public void The_walkthrough_always_counts_as_the_fourth_level_however_few_hints_come_first()
    {
        var levels = new List<int>();
        var cut = Ladder(["one", "two"], levels);

        for (var i = 0; i < 3; i++) cut.Find("button.hint-more").Click();

        Assert.Equal([1, 2, 4], levels);   // the solution is "shown the answer" whichever rung it is
        Assert.Equal(["Hint 1 of 2", "Hint 2 of 2", "Full solution"], Labels(cut));
    }

    [Fact]
    public void A_step_with_a_walkthrough_and_no_hints_still_has_a_ladder()
    {
        var levels = new List<int>();
        var cut = Ladder([], levels);

        Assert.Equal("Show the full solution", Button(cut));
        cut.Find("button.hint-more").Click();

        Assert.Equal([4], levels);
        Assert.Equal(["Full solution"], Labels(cut));
    }

    [Fact]
    public void A_resumed_attempt_that_reached_the_solution_reopens_every_rung_including_it()
    {
        var cut = Ladder(["one", "two"], initial: 4);

        Assert.Equal(["Hint 1 of 2", "Hint 2 of 2", "Full solution"], Labels(cut));
        Assert.Empty(cut.FindAll("button.hint-more"));
    }

    [Fact]
    public void A_resumed_attempt_that_stopped_at_a_hint_keeps_the_solution_closed()
    {
        var cut = Ladder(["one", "two"], initial: 2);

        Assert.Equal(["Hint 1 of 2", "Hint 2 of 2"], Labels(cut));
        Assert.Equal("Show the full solution", Button(cut));
    }

    // --- Rating ------------------------------------------------------------------------------------------------------

    private static ChoiceStep Question(params string[] hints) =>
        new("step.q", "Question", "Pick", false, [new ChoiceOption("right", true, null), new ChoiceOption("wrong", false, null)])
        { Hints = hints, Walkthrough = Solution };

    private Lesson LessonWith(ChoiceStep question) => new("lesson.l", "Lesson", "track.t", [question])
    {
        Concepts = [new Concept("concept.c", "C")],
        ReviewItems = [new ReviewItem("review.q", "concept.c", "step.q")],
    };

    private static JsonElement Data(ProgressEvent e) => JsonDocument.Parse(e.Data!).RootElement;

    [Fact]
    public void Opening_the_walkthrough_in_a_lesson_records_the_full_solution_level()
    {
        var lesson = LessonWith(Question("only hint"));
        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, lesson));

        cut.Find("button.hint-more").Click();
        cut.Find("button.hint-more").Click();

        var hints = _log.Items.Where(e => e.Type == ProgressEventTypes.HintUsed).ToList();
        Assert.Equal([1, 4], hints.Select(e => Data(e).GetProperty("level").GetInt32()));
        Assert.All(hints, e => Assert.Equal("step.q", e.StepId));
    }

    [Fact]
    public void Answering_right_after_the_walkthrough_in_review_is_rated_Again_even_with_a_single_hint_before_it()
    {
        var lesson = LessonWith(Question("only hint"));
        var content = new ContentGraph(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [lesson.Id])], [lesson]);
        _recorder.CompleteStep(lesson, "step.q");
        var cut = Render<ReviewView>(p => p.Add(c => c.Content, content));
        _clock.Now = T0.AddSeconds(2);

        cut.Find("button.hint-more").Click();   // the one hint
        cut.Find("button.hint-more").Click();   // the full solution
        cut.FindAll("ul.options input")[0].Change(true);
        cut.Find("button.primary").Click();

        var answer = Assert.Single(_log.Items, e => e.Type == ProgressEventTypes.ReviewAnswered);
        Assert.Equal((4, 1), (Data(answer).GetProperty("hintsUsed").GetInt32(), Data(answer).GetProperty("rating").GetInt32()));
    }

    [Fact]
    public void A_single_hint_without_the_walkthrough_is_still_only_Hard()
    {
        var lesson = LessonWith(Question("only hint"));
        var content = new ContentGraph(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [lesson.Id])], [lesson]);
        _recorder.CompleteStep(lesson, "step.q");
        var cut = Render<ReviewView>(p => p.Add(c => c.Content, content));
        _clock.Now = T0.AddSeconds(2);

        cut.Find("button.hint-more").Click();
        cut.FindAll("ul.options input")[0].Change(true);
        cut.Find("button.primary").Click();

        var answer = Assert.Single(_log.Items, e => e.Type == ProgressEventTypes.ReviewAnswered);
        Assert.Equal((1, 2), (Data(answer).GetProperty("hintsUsed").GetInt32(), Data(answer).GetProperty("rating").GetInt32()));
    }
}
