using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

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
}
