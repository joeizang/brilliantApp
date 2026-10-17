using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The Review screen: due items, answers recorded as ReviewAnswered events, and the entry points into it.</summary>
public class ReviewViewTests : ShortcutContext
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

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

    private static ChoiceStep Q(string id) =>
        new(id, "Question " + id, "Pick the right one", false, [new ChoiceOption("right", true, null), new ChoiceOption("wrong", false, null)]);

    private static readonly Lesson Lesson = new("lesson.l", "Lesson", "track.t",
        [new ExplainStep("step.intro", "Intro", "b", [], null), Q("step.q1"), Q("step.q2")])
    {
        Concepts = [new Concept("concept.c", "C")],
        ReviewItems = [new ReviewItem("review.q1", "concept.c", "step.q1"), new ReviewItem("review.q2", "concept.c", "step.q2")],
    };

    private static readonly ContentGraph Content = new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [Lesson.Id])], [Lesson]);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private readonly ProgressRecorder _recorder;

    public ReviewViewTests()
    {
        _recorder = new ProgressRecorder(_log, "device", _clock);
        Services.AddSingleton(_recorder);
    }

    private void FinishLesson()
    {
        foreach (var s in Lesson.Steps) _recorder.CompleteStep(Lesson, s.Id);
    }

    private IRenderedComponent<ReviewView> Open(Action? onExit = null) =>
        Render<ReviewView>(p => p.Add(c => c.Content, Content).Add(c => c.OnExit, () => onExit?.Invoke()));

    private static void Pick(IRenderedComponent<ReviewView> cut, string option)
    {
        var index = cut.FindAll("ul.options li").ToList().FindIndex(li => li.TextContent.Trim() == option);
        cut.FindAll("ul.options input")[index].Change(true);
        cut.Find("button.primary").Click();
    }

    private IEnumerable<ProgressEvent> Answers => _log.Items.Where(e => e.Type == ProgressEventTypes.ReviewAnswered);

    private static JsonElement Data(ProgressEvent e) => JsonDocument.Parse(e.Data!).RootElement;

    // A design-tradeoff review keeps the whole answer on the review event, so a local model can still give feedback on the justification.
    private static readonly Lesson TradeoffLesson = new("lesson.tr", "Tradeoffs", "track.t",
        [
            new ExplainStep("step.intro3", "Intro", "b", [], null),
            new DesignTradeoffStep("step.tr", "Where does the cache live?", "Three instances share an API.",
                [new TradeoffOption("In-process", false, "They disagree."), new TradeoffOption("Shared Redis", true, "One copy.")],
                [new RubricPoint("One consistent view", 2), new RubricPoint("Accepts the hop", 1)],
                "Use a shared cache.", ["Always faster."]),
        ])
    {
        Concepts = [new Concept("concept.tr", "TR")],
        ReviewItems = [new ReviewItem("review.tr", "concept.tr", "step.tr")],
    };

    private static readonly ContentGraph TradeoffContent = new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [TradeoffLesson.Id])], [TradeoffLesson]);

    [Fact]
    public void A_design_tradeoff_review_records_the_whole_answer_without_counting_it_as_a_lesson_attempt()
    {
        foreach (var s in TradeoffLesson.Steps) _recorder.CompleteStep(TradeoffLesson, s.Id);
        var cut = Render<ReviewView>(p => p.Add(c => c.Content, TradeoffContent));

        cut.Find("li[data-option='1'] input").Change(true);
        cut.Find("textarea.justification").Input("Every instance sees one copy.");
        cut.Find("button.primary").Click();                                  // Check
        cut.Find(".rubric-point[data-point='0'] input").Change(true);
        cut.Find("button.primary").Click();                                  // Done

        var answer = Assert.Single(Answers);
        var detail = Data(answer).GetProperty("answer");
        Assert.Equal(1, detail.GetProperty("option").GetInt32());
        Assert.Equal("Every instance sees one copy.", detail.GetProperty("justification").GetString());
        Assert.Equal([0], detail.GetProperty("pointsMade").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Empty(detail.GetProperty("redFlags").EnumerateArray());
        Assert.Equal(0.4 + 0.6 * (2.0 / 3), detail.GetProperty("score").GetDouble(), 10);
        Assert.DoesNotContain(_log.Items, e => e.Type == ProgressEventTypes.StepAnswered);
    }

    [Fact]
    public void Answers_that_carry_no_detail_are_recorded_exactly_as_before()
    {
        FinishLesson();
        var cut = Open();

        Pick(cut, "right");

        Assert.False(Data(Assert.Single(Answers)).TryGetProperty("answer", out _));
    }

    [Fact]
    public void Before_any_lesson_is_finished_there_is_nothing_to_review()
    {
        var cut = Open();

        Assert.Contains("Nothing to review yet", cut.Markup);
        Assert.Empty(cut.FindAll("ul.options"));
    }

    // Progress arriving from another device (sync) bumps SyncRevision; these cover what the screen does with that.
    private static readonly Lesson Second = new("lesson.m", "More", "track.t",
        [new ExplainStep("step.intro2", "Intro", "b", [], null), Q("step.q3")])
    {
        Concepts = [new Concept("concept.d", "D")],
        ReviewItems = [new ReviewItem("review.q3", "concept.d", "step.q3")],
    };

    private static readonly ContentGraph Both = new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [Lesson.Id, Second.Id])], [Lesson, Second]);

    private IRenderedComponent<ReviewView> OpenBoth(int revision = 0) =>
        Render<ReviewView>(p => p.Add(c => c.Content, Both).Add(c => c.SyncRevision, revision));

    private static void Synced(IRenderedComponent<ReviewView> cut, int revision) =>
        cut.Render(p => p.Add(c => c.Content, Both).Add(c => c.SyncRevision, revision));

    [Fact]
    public void An_empty_Review_fills_in_when_synced_progress_unlocks_questions()
    {
        var cut = OpenBoth();
        Assert.Contains("Nothing to review yet", cut.Markup);

        FinishLesson();   // as if pulled from the other device
        Synced(cut, 1);

        Assert.Contains("Question step.q1", cut.Markup);
        Assert.Contains("1 of 2", cut.Find(".review-progress").TextContent);
    }

    [Fact]
    public void A_finished_Review_picks_up_newly_due_questions_when_synced_progress_arrives()
    {
        FinishLesson();
        var cut = OpenBoth();
        Pick(cut, "right"); cut.Find("button.primary").Click();
        Pick(cut, "right"); cut.Find("button.primary").Click();
        Assert.Contains("all caught up", cut.Markup);

        foreach (var s in Second.Steps) _recorder.CompleteStep(Second, s.Id);
        Synced(cut, 1);

        Assert.Contains("Question step.q3", cut.Markup);
        Assert.Contains("1 of 1", cut.Find(".review-progress").TextContent);
    }

    [Fact]
    public void Synced_progress_does_not_disturb_a_question_being_answered_and_joins_the_queue_after_it()
    {
        FinishLesson();
        var cut = OpenBoth();
        var options = cut.FindAll("ul.options input");
        options[1].Change(true);   // an answer chosen but not yet checked
        Assert.Contains("Question step.q1", cut.Markup);

        foreach (var s in Second.Steps) _recorder.CompleteStep(Second, s.Id);
        Synced(cut, 1);

        Assert.Contains("Question step.q1", cut.Markup);
        Assert.Contains("1 of 2", cut.Find(".review-progress").TextContent);   // not reshuffled under the learner
        Assert.True(((AngleSharp.Html.Dom.IHtmlInputElement)cut.FindAll("ul.options input")[1]).IsChecked);   // the chosen option is still chosen

        Pick(cut, "right");
        cut.Find("button.primary").Click();   // Continue to q2
        Assert.Contains("2 of 3", cut.Find(".review-progress").TextContent);
        Pick(cut, "right");
        cut.Find("button.primary").Click();
        Assert.Contains("Question step.q3", cut.Markup);
        Assert.Contains("3 of 3", cut.Find(".review-progress").TextContent);
    }

    [Fact]
    public void A_sync_that_changes_nothing_about_the_queue_leaves_it_as_it_was()
    {
        FinishLesson();
        var cut = OpenBoth();
        Pick(cut, "right"); cut.Find("button.primary").Click();

        Synced(cut, 1);
        Pick(cut, "right"); cut.Find("button.primary").Click();

        Assert.Contains("all caught up", cut.Markup);
        Assert.Equal(2, Answers.Count());
    }

    [Fact]
    public void A_finished_lesson_puts_its_items_in_the_queue_one_at_a_time()
    {
        FinishLesson();

        var cut = Open();

        Assert.Contains("Question step.q1", cut.Markup);
        Assert.Contains("1 of 2", cut.Find(".review-progress").TextContent);
    }

    [Fact]
    public void A_review_question_can_be_flagged_against_its_step_without_answering_it()
    {
        FinishLesson();
        var cut = Open();

        cut.Find("button.flag-open").Click();
        cut.Find("textarea.flag-note").Input("both options look right");
        cut.Find("button.flag-save").Click();

        var flag = Assert.Single(_log.Items, e => e.Type == ProgressEventTypes.StepFlagged);
        Assert.Equal(("lesson.l", "step.q1"), (flag.LessonId, flag.StepId));
        Assert.Equal("both options look right", Data(flag).GetProperty("note").GetString());
        Assert.Empty(Answers);
        Assert.Contains("Flagged", cut.Find(".flag-saved").TextContent);
    }

    [Fact]
    public void Moving_to_the_next_review_question_offers_a_fresh_flag_control()
    {
        FinishLesson();
        var cut = Open();
        cut.Find("button.flag-open").Click();
        cut.Find("button.flag-save").Click();

        Pick(cut, "right");
        cut.Find("button.primary").Click();   // Continue

        Assert.Contains("Question step.q2", cut.Markup);
        Assert.NotEmpty(cut.FindAll("button.flag-open"));
        Assert.Empty(cut.FindAll(".flag-saved"));
    }

    [Fact]
    public void A_quick_correct_answer_is_recorded_as_easy_and_not_as_lesson_progress()
    {
        FinishLesson();
        var before = _log.Items.Count;
        var cut = Open();
        _clock.Now = T0.AddSeconds(2);

        Pick(cut, "right");

        var answer = Assert.Single(Answers);
        Assert.Equal(before + 1, _log.Items.Count);
        Assert.Equal(("review.q1", true, 4, 0), (Data(answer).GetProperty("item").GetString(), Data(answer).GetProperty("correct").GetBoolean(), Data(answer).GetProperty("rating").GetInt32(), Data(answer).GetProperty("hintsUsed").GetInt32()));
        Assert.Equal(2000, Data(answer).GetProperty("elapsedMs").GetInt32());
        Assert.Equal("lesson.l", answer.LessonId);
    }

    [Fact]
    public void A_slow_correct_answer_is_recorded_as_hard()
    {
        FinishLesson();
        var cut = Open();
        _clock.Now = T0.AddSeconds(45);

        Pick(cut, "right");

        Assert.Equal(2, Data(Assert.Single(Answers)).GetProperty("rating").GetInt32());
    }

    [Fact]
    public void A_wrong_first_answer_is_Again_and_retrying_does_not_record_a_second_answer()
    {
        FinishLesson();
        var cut = Open();

        Pick(cut, "wrong");
        cut.Find("button.primary").Click();   // Try again
        Pick(cut, "right");

        var answer = Assert.Single(Answers);
        Assert.False(Data(answer).GetProperty("correct").GetBoolean());
        Assert.Equal(1, Data(answer).GetProperty("rating").GetInt32());
    }

    [Fact]
    public void After_answering_the_learner_is_told_when_they_will_see_the_item_again()
    {
        FinishLesson();
        var cut = Open();
        _clock.Now = T0.AddSeconds(15);

        Pick(cut, "right");

        Assert.Contains("see this again in 3 days", cut.Find(".review-next").TextContent);
    }

    [Fact]
    public void Continuing_moves_through_the_queue_and_ends_caught_up()
    {
        FinishLesson();
        var cut = Open();

        Pick(cut, "right");
        cut.Find("button.primary").Click();   // Continue
        Assert.Contains("Question step.q2", cut.Markup);
        Assert.Contains("2 of 2", cut.Find(".review-progress").TextContent);

        Pick(cut, "right");
        cut.Find("button.primary").Click();

        Assert.Contains("all caught up", cut.Markup);
        Assert.Contains("Next review", cut.Markup);
        Assert.Equal(2, Answers.Count());
    }

    [Fact]
    public void Items_answered_earlier_come_back_once_they_are_due()
    {
        FinishLesson();
        var first = Open();
        Pick(first, "right");
        first.Find("button.primary").Click();
        Pick(first, "right");

        _clock.Now = T0.AddDays(30);
        var later = Open();

        Assert.Contains("1 of 2", later.Find(".review-progress").TextContent);
    }

    [Fact]
    public void Back_leaves_the_review_screen()
    {
        FinishLesson();
        var exited = false;
        var cut = Open(() => exited = true);

        cut.Find("button.link").Click();

        Assert.True(exited);
    }

    [Fact]
    public void The_tracks_screen_offers_review_with_the_number_due_and_opens_it()
    {
        FinishLesson();
        var opened = false;

        var cut = Render<TracksView>(p => p.Add(c => c.Content, Content).Add(c => c.OnOpenReview, () => opened = true));
        var button = cut.Find("button.review-card");
        button.Click();

        Assert.Contains("2 due", button.TextContent);
        Assert.True(opened);
    }

    [Fact]
    public void The_tracks_screen_says_when_no_reviews_are_due()
    {
        var cut = Render<TracksView>(p => p.Add(c => c.Content, Content));

        Assert.Empty(cut.FindAll("button.review-card"));
        Assert.Contains("No reviews due", cut.Markup);
    }

    [Fact]
    public void The_tracks_screen_links_to_Sync_for_windows_too_narrow_for_the_sidebar()
    {
        var opened = false;

        var cut = Render<TracksView>(p => p.Add(c => c.Content, Content).Add(c => c.OnOpenSync, () => opened = true));
        cut.Find("button.sync-link").Click();

        Assert.True(opened);
        Assert.Empty(cut.FindAll("button.flagged-link"));
    }

    [Fact]
    public void The_tracks_screen_has_no_Sync_link_when_nothing_handles_it()
    {
        var cut = Render<TracksView>(p => p.Add(c => c.Content, Content));

        Assert.Empty(cut.FindAll("button.sync-link"));
    }

    [Fact]
    public void The_sidebar_shows_the_due_count_next_to_Review()
    {
        FinishLesson();

        var cut = Render<CourseSidebar>(p => p.Add(c => c.Content, Content));

        Assert.Contains("2", cut.Find("button.side-review").TextContent);
    }

    [Fact]
    public void Opening_review_from_the_shell_shows_the_queue_and_Back_returns_to_Today()
    {
        FinishLesson();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/shortcuts.js");
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Content));

        cut.Find("button.review-card").Click();
        Assert.Contains("Question step.q1", cut.Markup);

        cut.Find("button.link").Click();
        Assert.Equal("Today", cut.Find("main h1").TextContent.Trim());
    }

    [Fact]
    public void Answering_in_the_shell_refreshes_the_sidebars_due_count()
    {
        FinishLesson();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/shortcuts.js");
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Content));
        cut.Find("button.review-card").Click();
        Assert.Equal("2", cut.Find(".due-count").TextContent.Trim());

        var index = cut.FindAll("ul.options li").ToList().FindIndex(li => li.TextContent.Trim() == "right");
        cut.FindAll("ul.options input")[index].Change(true);
        cut.Find("button.primary").Click();

        Assert.Equal("1", cut.Find(".due-count").TextContent.Trim());
    }

    [Fact]
    public void The_sidebar_badge_disappears_once_the_queue_is_empty()
    {
        FinishLesson();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/shortcuts.js");
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Content));
        cut.Find("button.review-card").Click();

        for (var i = 0; i < 2; i++)
        {
            var index = cut.FindAll("ul.options li").ToList().FindIndex(li => li.TextContent.Trim() == "right");
            cut.FindAll("ul.options input")[index].Change(true);
            cut.Find("button.primary").Click();   // Check
            cut.Find("button.primary").Click();   // Continue
        }

        Assert.Contains("all caught up", cut.Markup);
        Assert.Empty(cut.FindAll(".due-count"));
    }
}
