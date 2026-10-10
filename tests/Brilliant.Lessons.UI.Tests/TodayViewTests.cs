using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The Today screen, the daily session that runs from it, its summary, and Today as the app's home.</summary>
public class TodayViewTests : ShortcutContext
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

    private static ExplainStep Explain(string id) => new(id, "Title " + id, "body", [], null);

    // Basics has a review item; Next is the lesson a session continues with.
    private static readonly Lesson Basics = new("lesson.basics", "Basics", "track.t", [Explain("step.intro"), Q("step.q")])
    {
        Concepts = [new Concept("concept.basics", "Basic idea")],
        ReviewItems = [new ReviewItem("review.q", "concept.basics", "step.q")],
    };
    private static readonly Lesson Next = new("lesson.next", "Next up", "track.t", [Explain("step.n1"), Explain("step.n2")]);

    private static ContentGraph Graph(params Lesson[] lessons) =>
        new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "Tools", lessons.Select(l => l.Id).ToList())], lessons);

    private static readonly ContentGraph Content = Graph(Basics, Next);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private readonly ProgressRecorder _recorder;

    public TodayViewTests()
    {
        _recorder = new ProgressRecorder(_log, "device", _clock);
        Services.AddSingleton(_recorder);
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/shortcuts.js");
    }

    private void Finish(Lesson lesson)
    {
        foreach (var s in lesson.Steps) _recorder.CompleteStep(lesson, s.Id);
    }

    /// <summary>Basics done an hour ago, so its review is due and Next is the lesson to continue with.</summary>
    private void ReadyForASession()
    {
        Finish(Basics);
        _clock.Now = T0.AddHours(1);
    }

    private IEnumerable<ProgressEvent> Events(string type) => _log.Items.Where(e => e.Type == type);

    private IRenderedComponent<TodayView> OpenToday(Action? onStart = null, Action? onBrowse = null, Action? onReview = null) =>
        Render<TodayView>(p => p.Add(c => c.Content, Content)
            .Add(c => c.OnStart, () => onStart?.Invoke())
            .Add(c => c.OnBrowse, () => onBrowse?.Invoke())
            .Add(c => c.OnOpenReview, () => onReview?.Invoke()));

    private IRenderedComponent<SessionRunner> OpenSession(Action? onExit = null) =>
        Render<SessionRunner>(p => p.Add(c => c.Content, Content).Add(c => c.OnExit, () => onExit?.Invoke()));

    // Answers the review question that is showing, and moves past it.
    private static void AnswerReview(IRenderedComponent<SessionRunner> cut, string option = "right")
    {
        var index = cut.FindAll("ul.options li").ToList().FindIndex(li => li.TextContent.Trim() == option);
        cut.FindAll("ul.options input")[index].Change(true);
        cut.Find("button.primary").Click();      // Check
        cut.Find("button.primary").Click();      // Continue
    }

    // --- The Today screen ---------------------------------------------------------------------------------------------

    [Fact]
    public void Today_shows_the_goal_progress_streak_reviews_and_the_next_lesson()
    {
        _recorder.SetDailyGoal(5);
        Finish(Basics);                                                    // two steps today
        var cut = OpenToday();

        Assert.Equal("Today", cut.Find("h1").TextContent.Trim());
        var goal = cut.Find(".today-goal [role=progressbar]");
        Assert.Equal(("2", "5"), (goal.GetAttribute("aria-valuenow"), goal.GetAttribute("aria-valuemax")));
        Assert.Contains("2 of 5 steps", cut.Find(".today-goal").TextContent);
        Assert.Contains("1 review due", cut.Find("button.review-card").TextContent);
        Assert.Contains("Next up", cut.Find(".today-next").TextContent);
        Assert.Contains("Tools", cut.Find(".today-next").TextContent);
        Assert.Contains("No streak yet", cut.Find(".today-streak").TextContent);
    }

    [Fact]
    public void The_streak_is_shown_in_days()
    {
        _clock.Now = T0.AddDays(-3);
        _recorder.SetDailyGoal(1);
        _clock.Now = T0.AddDays(-2);
        _recorder.StepCompleted("lesson.next", "step.n1");
        _clock.Now = T0.AddDays(-1);
        _recorder.StepCompleted("lesson.next", "step.n1");
        _clock.Now = T0;

        Assert.Contains("2-day streak", OpenToday().Find(".today-streak").TextContent);

        _recorder.StepCompleted("lesson.next", "step.n2");
        Assert.Contains("3-day streak", OpenToday().Find(".today-streak").TextContent);
    }

    [Fact]
    public void Meeting_the_goal_is_acknowledged()
    {
        _recorder.SetDailyGoal(2);
        Finish(Basics);

        var cut = OpenToday();

        Assert.Contains("Goal met", cut.Find(".today-goal").TextContent);
    }

    [Fact]
    public void The_goal_can_be_raised_and_lowered_in_fives_and_is_recorded()
    {
        var cut = OpenToday();
        Assert.Contains("0 of 10 steps", cut.Find(".today-goal").TextContent);

        _clock.Now = T0.AddMinutes(1);                                      // the latest setting wins, so each click needs its own moment
        cut.Find("button.goal-up").Click();
        Assert.Contains("0 of 15 steps", cut.Find(".today-goal").TextContent);

        _clock.Now = T0.AddMinutes(2);
        cut.Find("button.goal-down").Click();
        _clock.Now = T0.AddMinutes(3);
        cut.Find("button.goal-down").Click();
        Assert.Contains("0 of 5 steps", cut.Find(".today-goal").TextContent);
        Assert.Equal([15, 10, 5], Events(ProgressEventTypes.DailyGoalSet).Select(e => System.Text.Json.JsonDocument.Parse(e.Data!).RootElement.GetProperty("steps").GetInt32()));
    }

    [Fact]
    public void The_goal_stops_at_five_and_a_hundred()
    {
        _recorder.SetDailyGoal(5);
        Assert.True(OpenToday().Find("button.goal-down").HasAttribute("disabled"));

        _clock.Now = T0.AddMinutes(1);
        _recorder.SetDailyGoal(100);
        var cut = OpenToday();
        Assert.True(cut.Find("button.goal-up").HasAttribute("disabled"));
        Assert.False(cut.Find("button.goal-down").HasAttribute("disabled"));
    }

    [Fact]
    public void The_session_button_starts_resumes_or_offers_another()
    {
        ReadyForASession();
        Assert.Equal("Start today's session", OpenToday().Find("button.start-session").TextContent.Trim());

        _recorder.StartSession("lesson.next");
        Assert.Equal("Resume session", OpenToday().Find("button.start-session").TextContent.Trim());

        _clock.Now = T0.AddHours(2);
        _recorder.SetDailyGoal(1);                                          // goal met, but the session is still open
        Assert.Equal("Resume session", OpenToday().Find("button.start-session").TextContent.Trim());

        _recorder.CompleteSession();
        Assert.Equal("Do another session", OpenToday().Find("button.start-session").TextContent.Trim());
    }

    [Fact]
    public void A_goal_off_the_five_step_grid_snaps_to_the_bounds()
    {
        _recorder.SetDailyGoal(7);
        var cut = OpenToday();

        _clock.Now = T0.AddMinutes(1);
        cut.Find("button.goal-down").Click();

        Assert.Contains("0 of 5 steps", cut.Find(".today-goal").TextContent);
    }

    [Fact]
    public void Lesson_steps_done_before_the_session_do_not_skip_its_review()
    {
        Finish(Basics);
        _recorder.CompleteStep(Next, "step.n1");                            // started the next lesson earlier today
        _clock.Now = T0.AddHours(1);

        var cut = OpenSession();

        Assert.Equal("Review", cut.Find("h1").TextContent.Trim());
    }

    [Fact]
    public void A_lesson_finished_elsewhere_during_review_goes_straight_to_the_summary()
    {
        ReadyForASession();
        var cut = OpenSession();
        _clock.Now = T0.AddHours(2);
        Finish(Next);                                                       // e.g. on another device

        AnswerReview(cut);
        cut.Find(".review-actions button.primary").Click();

        Assert.Equal("Session complete", cut.Find("h1").TextContent.Trim());
    }

    [Fact]
    public void The_session_button_and_browse_button_raise_their_callbacks()
    {
        ReadyForASession();
        string? clicked = null;
        var cut = OpenToday(onStart: () => clicked = "start", onBrowse: () => clicked = "browse", onReview: () => clicked = "review");

        cut.Find("button.start-session").Click();
        Assert.Equal("start", clicked);
        cut.Find("button.browse").Click();
        Assert.Equal("browse", clicked);
        cut.Find("button.review-card").Click();
        Assert.Equal("review", clicked);
    }

    [Fact]
    public void With_nothing_left_to_do_Today_says_so_and_offers_no_session()
    {
        Finish(Basics);
        Finish(Next);
        _recorder.ReviewAnswered(Basics.Id, Basics.ReviewItems[0], true, 0, TimeSpan.FromSeconds(2), Brilliant.Core.Review.Rating.Good);
        _clock.Now = T0.AddMinutes(5);

        var cut = OpenToday();

        Assert.Contains("all caught up", cut.Find(".today-empty").TextContent);
        Assert.Empty(cut.FindAll("button.start-session"));
        Assert.Empty(cut.FindAll("button.review-card"));
        Assert.NotNull(cut.Find("button.browse"));
    }

    // --- The session ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_session_starts_with_review_then_the_lesson_then_the_summary()
    {
        ReadyForASession();
        var cut = OpenSession();

        Assert.Equal("lesson.next", Assert.Single(Events(ProgressEventTypes.SessionStarted)).LessonId);
        Assert.Equal("Review", cut.Find("h1").TextContent.Trim());
        Assert.Equal("Review", cut.Find(".session-steps [aria-current=step]").TextContent.Trim());
        AnswerReview(cut);
        Assert.Equal("Continue to lesson", cut.Find(".review-actions button.primary").TextContent.Trim());

        cut.Find(".review-actions button.primary").Click();
        Assert.Equal("Next up", cut.Find("h1").TextContent.Trim());
        Assert.Equal("Lesson", cut.Find(".session-steps [aria-current=step]").TextContent.Trim());
        cut.Find(".step-actions button.primary").Click();
        cut.Find(".step-actions button.primary").Click();
        Assert.Equal("See summary", cut.Find(".step-actions button.primary").TextContent.Trim());

        cut.Find(".step-actions button.primary").Click();
        Assert.Equal("Session complete", cut.Find("h1").TextContent.Trim());
        Assert.Equal("Summary", cut.Find(".session-steps [aria-current=step]").TextContent.Trim());
        Assert.Single(Events(ProgressEventTypes.SessionCompleted));
    }

    [Fact]
    public void With_no_reviews_due_the_session_goes_straight_to_the_lesson()
    {
        Finish(Basics);
        _recorder.ReviewAnswered(Basics.Id, Basics.ReviewItems[0], true, 0, TimeSpan.FromSeconds(2), Brilliant.Core.Review.Rating.Good);
        _clock.Now = T0.AddMinutes(5);

        var cut = OpenSession();

        Assert.Equal("Next up", cut.Find("h1").TextContent.Trim());
    }

    [Fact]
    public void With_no_lesson_left_the_session_is_just_review_then_the_summary()
    {
        Finish(Basics);
        Finish(Next);
        _clock.Now = T0.AddHours(1);
        var cut = OpenSession();
        Assert.Equal("", Events(ProgressEventTypes.SessionStarted).Single().LessonId);

        AnswerReview(cut);

        Assert.Equal("See summary", cut.Find(".review-actions button.primary").TextContent.Trim());
        cut.Find(".review-actions button.primary").Click();
        Assert.Equal("Session complete", cut.Find("h1").TextContent.Trim());
    }

    [Fact]
    public void Leaving_part_way_keeps_the_session_and_coming_back_resumes_in_the_lesson()
    {
        ReadyForASession();
        var exited = false;
        var cut = OpenSession(() => exited = true);
        AnswerReview(cut);
        cut.Find(".review-actions button.primary").Click();
        cut.Find(".step-actions button.primary").Click();                   // first lesson step done
        cut.Find(".lesson-bar button.link").Click();                        // "← Back"

        Assert.True(exited);
        Assert.Empty(Events(ProgressEventTypes.SessionCompleted));

        var again = OpenSession();

        Assert.Single(Events(ProgressEventTypes.SessionStarted));           // resumed, not restarted
        Assert.Equal("Lesson", again.Find(".session-steps [aria-current=step]").TextContent.Trim());
        Assert.Contains("Title step.n2", again.Markup);                     // exactly where it was left
    }

    [Fact]
    public void Leaving_during_review_resumes_in_review_with_what_is_left()
    {
        ReadyForASession();
        var cut = OpenSession();
        cut.Find(".review-bar button.link").Click();

        var again = OpenSession();

        Assert.Single(Events(ProgressEventTypes.SessionStarted));
        Assert.Equal("Review", again.Find("h1").TextContent.Trim());
    }

    [Fact]
    public void Reopening_after_the_lesson_was_finished_goes_to_the_summary_once()
    {
        ReadyForASession();
        _recorder.StartSession("lesson.next");
        _clock.Now = T0.AddHours(2);
        Finish(Next);

        var cut = OpenSession();
        Assert.Equal("Session complete", cut.Find("h1").TextContent.Trim());
        cut.Render();
        _clock.Now = T0.AddHours(3);
        OpenSession();                                                      // a second visit finds the session already closed and starts a new one

        Assert.Single(Events(ProgressEventTypes.SessionCompleted));
    }

    [Fact]
    public void The_summary_reports_steps_reviews_accuracy_mastery_and_the_goal()
    {
        _recorder.SetDailyGoal(5);
        ReadyForASession();
        var cut = OpenSession();
        AnswerReview(cut);
        cut.Find(".review-actions button.primary").Click();
        cut.Find(".step-actions button.primary").Click();
        cut.Find(".step-actions button.primary").Click();
        cut.Find(".step-actions button.primary").Click();                   // See summary

        var stats = cut.Find(".summary-stats").TextContent;
        Assert.Matches(@"Steps\s*2", stats);
        Assert.Matches(@"Reviews\s*1", stats);
        Assert.Matches(@"Accuracy\s*100%", stats);
        var change = Assert.Single(cut.FindAll(".summary-changes li"));
        Assert.Contains("Basic idea", change.TextContent);
        Assert.Contains("%", change.TextContent);
        Assert.Contains("up", change.ClassList);
        Assert.Contains("5 of 5 steps", cut.Find(".summary-goal").TextContent);   // today: Basics' 2 steps earlier, the lesson's 2 and the review
        Assert.Contains("Goal met", cut.Find(".summary-goal").TextContent);
    }

    [Fact]
    public void An_empty_summary_has_no_accuracy_and_says_nothing_moved()
    {
        var cut = Render<SessionSummaryView>(p => p
            .Add(c => c.Summary, new SessionSummary(0, 0, 0, 0, []))
            .Add(c => c.Today, new TodayState(10, 0, 0, null, 0, null)));

        Assert.Matches(@"Accuracy\s*—", cut.Find(".summary-stats").TextContent);
        Assert.Contains("No mastery changes", cut.Markup);
    }

    [Fact]
    public void A_summary_that_meets_the_goal_shows_the_streak()
    {
        var cut = Render<SessionSummaryView>(p => p
            .Add(c => c.Summary, new SessionSummary(3, 2, 4, 3, []))
            .Add(c => c.Today, new TodayState(3, 5, 4, null, 0, null)));

        Assert.Contains("Goal met", cut.Find(".summary-goal").TextContent);
        Assert.Contains("4-day streak", cut.Find(".summary-goal").TextContent);
        Assert.Matches(@"Accuracy\s*75%", cut.Find(".summary-stats").TextContent);
    }

    [Fact]
    public void The_summarys_done_button_raises_OnDone()
    {
        var done = false;
        var cut = Render<SessionSummaryView>(p => p
            .Add(c => c.Summary, new SessionSummary(0, 0, 0, 0, []))
            .Add(c => c.Today, new TodayState(10, 0, 0, null, 0, null))
            .Add(c => c.OnDone, () => done = true));

        cut.Find("button.primary").Click();

        Assert.True(done);
    }

    // --- Today as home -------------------------------------------------------------------------------------------------

    private IRenderedComponent<CourseShell> OpenShell() => Render<CourseShell>(p => p.Add(c => c.Content, Content));

    [Fact]
    public async Task The_app_opens_on_Today_and_Back_from_the_tracks_returns_to_it()
    {
        var cut = OpenShell();
        Assert.Equal("Today", cut.Find("main h1").TextContent.Trim());

        cut.Find("button.browse").Click();
        Assert.Equal("Tracks", cut.Find("main h1").TextContent.Trim());

        await Press(StepCommand.Back);
        Assert.Equal("Today", cut.Find("main h1").TextContent.Trim());

        await Press(StepCommand.Back);                                      // already at the top
        Assert.Equal("Today", cut.Find("main h1").TextContent.Trim());
    }

    [Fact]
    public void The_tracks_list_has_a_way_back_to_Today()
    {
        var cut = OpenShell();
        cut.Find("button.browse").Click();

        cut.Find("main .crumbs button").Click();

        Assert.Equal("Today", cut.Find("main h1").TextContent.Trim());
    }

    [Fact]
    public void The_sidebar_has_a_Today_item_that_is_current_on_Today()
    {
        var cut = OpenShell();
        Assert.Contains("current", cut.Find("aside button.side-today").ClassList);

        cut.Find("button.browse").Click();
        Assert.DoesNotContain("current", cut.Find("aside button.side-today").ClassList);

        cut.Find("aside button.side-today").Click();
        Assert.Equal("Today", cut.Find("main h1").TextContent.Trim());
    }

    [Fact]
    public async Task Starting_the_session_from_Today_and_stepping_back_leaves_it_to_resume()
    {
        ReadyForASession();
        var cut = OpenShell();

        cut.Find("button.start-session").Click();
        Assert.Equal("Review", cut.Find("main h1").TextContent.Trim());

        await Press(StepCommand.Back);
        Assert.Equal("Today", cut.Find("main h1").TextContent.Trim());
        Assert.Equal("Resume session", cut.Find("button.start-session").TextContent.Trim());
    }

    [Fact]
    public void Finishing_a_session_returns_to_Today_with_the_work_counted()
    {
        _recorder.SetDailyGoal(5);
        ReadyForASession();
        var cut = OpenShell();
        cut.Find("button.start-session").Click();

        cut.FindAll("ul.options input")[0].Change(true);
        cut.Find("button.primary").Click();
        cut.Find("button.primary").Click();
        cut.Find(".review-actions button.primary").Click();
        cut.Find(".step-actions button.primary").Click();
        cut.Find(".step-actions button.primary").Click();
        cut.Find(".step-actions button.primary").Click();
        Assert.Equal("Session complete", cut.Find("main h1").TextContent.Trim());
        cut.Find("main .summary-done").Click();

        Assert.Equal("Today", cut.Find("main h1").TextContent.Trim());
        Assert.Contains("steps", cut.Find(".today-goal").TextContent);
        Assert.Contains("Goal met", cut.Find(".today-goal").TextContent);
        Assert.Contains("all caught up", cut.Find(".today-empty").TextContent);   // reviews done, every lesson finished
    }
}
