using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>The daily goal, the streak of days that met it, the next lesson and the session, all derived from the log.</summary>
public class TodayTests
{
    // Noon on Saturday, in a +02:00 zone so "the learner's day" differs from UTC's.
    private static readonly TimeSpan Zone = TimeSpan.FromHours(2);
    private static readonly DateTimeOffset Noon = new(2026, 10, 10, 12, 0, 0, Zone);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Noon;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static ChoiceStep Choice(string id) =>
        new(id, id, "?", false, [new ChoiceOption("a", true, null), new ChoiceOption("b", false, null)]);

    private static ExplainStep Explain(string id) => new(id, id, "b", [], null);

    private static readonly Lesson L1 = new("lesson.1", "One", "track.a", [Explain("step.1.intro"), Choice("step.1.q")])
    {
        Concepts = [new Concept("concept.1", "One")],
        ReviewItems = [new ReviewItem("review.1", "concept.1", "step.1.q")],
    };
    private static readonly Lesson L2 = new("lesson.2", "Two", "track.a", [Explain("step.2.intro"), Explain("step.2.more")]);
    private static readonly Lesson L3 = new("lesson.3", "Three", "track.b", [Explain("step.3.intro")]);

    private static readonly ContentGraph Content = new(new PackManifest("pack.t", "1.0.0", 1),
        [new Track("track.a", "A", [L1.Id, L2.Id]), new Track("track.b", "B", [L3.Id])], [L1, L2, L3]);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    private TodayState Today(DateTimeOffset? now = null) =>
        LearnerStateProjector.Project(_log.ReadAll(), Content, now ?? Noon).Today;

    private int _unique;

    /// <summary>Completes <paramref name="count"/> distinct steps at noon, <paramref name="daysAgo"/> days before "today".</summary>
    private void Active(int daysAgo, int count)
    {
        _clock.Now = Noon.AddDays(-daysAgo);
        for (var i = 0; i < count; i++) Recorder.StepCompleted(L2.Id, $"step.x.{_unique++}");
        _clock.Now = Noon;
    }

    /// <summary>Sets the daily goal <paramref name="daysAgo"/> days back (long ago by default, so it covers the days under test).</summary>
    private void Goal(int steps, int daysAgo = 500)
    {
        _clock.Now = Noon.AddDays(-daysAgo);
        Recorder.SetDailyGoal(steps);
        _clock.Now = Noon;
    }

    // --- Goal and today's progress ----------------------------------------------------------------------------------------

    [Fact]
    public void With_nothing_done_the_goal_is_the_default_and_unmet()
    {
        var t = Today();

        Assert.Equal((TodayState.DefaultGoal, 0, false, 0), (t.GoalSteps, t.DoneToday, t.GoalMet, t.Streak));
        Assert.Equal(10, TodayState.DefaultGoal);
    }

    [Fact]
    public void Steps_completed_and_reviews_answered_today_count_toward_the_goal()
    {
        Recorder.StepCompleted(L1.Id, "step.1.intro");
        Recorder.StepCompleted(L1.Id, "step.1.q");
        Recorder.ReviewAnswered(L1.Id, L1.ReviewItems[0], true, 0, TimeSpan.FromSeconds(2), Rating.Good);
        Recorder.StepAnswered(L1.Id, "step.1.q", true, [0]);   // an answer is not progress by itself

        Assert.Equal(3, Today().DoneToday);
    }

    [Fact]
    public void Doing_the_same_step_again_the_same_day_counts_once()
    {
        Recorder.StepCompleted(L2.Id, "step.2.intro");
        Recorder.StepCompleted(L2.Id, "step.2.intro");
        Recorder.StepCompleted(L2.Id, "step.2.more");

        Assert.Equal(2, Today().DoneToday);
    }

    [Fact]
    public void Yesterdays_work_does_not_count_today()
    {
        Active(1, 4);
        Active(0, 2);

        Assert.Equal(2, Today().DoneToday);
    }

    [Fact]
    public void The_learners_day_follows_their_offset_not_utc()
    {
        // 23:30 UTC on the 9th is 01:30 on the 10th at +02:00: today's work.
        _clock.Now = new DateTimeOffset(2026, 10, 9, 23, 30, 0, TimeSpan.Zero);
        Recorder.StepCompleted(L2.Id, "step.2.intro");
        _clock.Now = Noon;

        Assert.Equal(1, Today().DoneToday);
        Assert.Equal(0, Today(Noon.ToUniversalTime()).DoneToday);    // the same instant as seen from a UTC day: it's yesterday
    }

    [Fact]
    public void Setting_the_goal_changes_it_and_the_latest_setting_wins()
    {
        Recorder.SetDailyGoal(25);
        _clock.Now = Noon.AddMinutes(5);
        Recorder.SetDailyGoal(15);

        Assert.Equal(15, Today(Noon.AddMinutes(10)).GoalSteps);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(100000, 500)]
    public void The_goal_is_kept_within_sensible_bounds(int asked, int expected)
    {
        Recorder.SetDailyGoal(asked);

        Assert.Equal(expected, Today().GoalSteps);
    }

    [Fact]
    public void A_malformed_goal_event_is_ignored()
    {
        _log.Append(new ProgressEvent("bad", "device", Noon, ProgressEventTypes.DailyGoalSet, "", "", "{\"steps\":\"lots\"}"));
        _log.Append(new ProgressEvent("none", "device", Noon, ProgressEventTypes.DailyGoalSet, "", "", null));

        Assert.Equal(TodayState.DefaultGoal, Today().GoalSteps);
    }

    [Fact]
    public void The_goal_is_met_at_exactly_the_goal()
    {
        Recorder.SetDailyGoal(3);
        Active(0, 2);
        Assert.False(Today().GoalMet);

        Active(0, 1);
        Assert.True(Today().GoalMet);
    }

    // --- Streak ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_streak_counts_consecutive_days_that_met_the_goal_up_to_today()
    {
        Goal(3);
        foreach (var d in new[] { 0, 1, 2 }) Active(d, 3);

        Assert.Equal(3, Today().Streak);
    }

    [Fact]
    public void A_day_that_has_not_met_the_goal_yet_does_not_break_the_streak()
    {
        Goal(3);
        Active(1, 3);
        Active(2, 3);
        Active(0, 1);        // today so far: not there yet

        var t = Today();

        Assert.Equal(2, t.Streak);
        Assert.False(t.GoalMet);
    }

    [Fact]
    public void Meeting_todays_goal_extends_the_streak_by_one()
    {
        Goal(3);
        Active(1, 3);
        Assert.Equal(1, Today().Streak);

        Active(0, 3);

        Assert.Equal(2, Today().Streak);
    }

    [Fact]
    public void A_missed_day_breaks_the_streak()
    {
        Goal(3);
        Active(3, 3);
        Active(2, 3);
        // yesterday: nothing

        Assert.Equal(0, Today().Streak);

        Active(0, 3);
        Assert.Equal(1, Today().Streak);
    }

    [Fact]
    public void A_day_short_of_the_goal_does_not_count()
    {
        Goal(3);
        Active(0, 3);
        Active(1, 2);          // one short
        Active(2, 3);

        Assert.Equal(1, Today().Streak);
    }

    [Fact]
    public void Each_past_day_is_judged_by_the_goal_in_force_at_the_time()
    {
        Goal(2, 4);
        Active(3, 2);
        Active(2, 2);
        Active(1, 2);
        Goal(20, 0);     // today: a much higher bar, which must not unmake the earlier days

        var t = Today();

        Assert.Equal(3, t.Streak);
        Assert.False(t.GoalMet);
    }

    [Fact]
    public void Lowering_the_goal_does_not_retroactively_rescue_a_day_that_missed_it()
    {
        Goal(10, 2);
        Active(1, 4);                  // yesterday: 4 of 10
        Goal(3, 0);
        Active(0, 3);

        Assert.Equal(1, Today().Streak);
    }

    [Fact]
    public void A_long_streak_is_counted_in_full()
    {
        Goal(1, 500);
        for (var d = 0; d < 400; d++) Active(d, 1);

        Assert.Equal(400, Today().Streak);
    }

    [Fact]
    public void Duplicate_events_and_event_order_change_nothing()
    {
        Goal(2);
        Active(1, 2);
        Active(0, 2);
        var events = _log.ReadAll().ToList();

        var t = LearnerStateProjector.Project(events.Concat(events).Reverse(), Content, Noon).Today;

        Assert.Equal((2, 2, 2), (t.GoalSteps, t.DoneToday, t.Streak));
    }

    // --- Next lesson and what is due -----------------------------------------------------------------------------------------

    [Fact]
    public void The_next_lesson_is_the_first_one_to_start_when_nothing_is_under_way()
    {
        Assert.Equal("lesson.1", Today().NextLesson!.Lesson.Id);
    }

    [Fact]
    public void A_lesson_under_way_comes_before_one_not_yet_started()
    {
        Recorder.CompleteStep(L1, "step.1.intro");
        Recorder.CompleteStep(L1, "step.1.q");                // L1 done; L2 now open
        Recorder.CompleteStep(L3, "step.3.intro");            // L3 is done; start L2 partially...
        Recorder.StepCompleted(L2.Id, "step.2.intro");        // ... L2 under way

        Assert.Equal("lesson.2", Today().NextLesson!.Lesson.Id);
    }

    [Fact]
    public void An_in_progress_lesson_wins_over_an_earlier_untouched_one()
    {
        var l3 = new Lesson("lesson.3", "Three", "track.b", [Explain("step.3.intro"), Explain("step.3.more")]);
        var content = new ContentGraph(Content.Manifest, Content.Tracks, [L1, L2, l3]);
        Recorder.StepCompleted(l3.Id, "step.3.intro");

        var next = LearnerStateProjector.Project(_log.ReadAll(), content, Noon).Today.NextLesson;

        Assert.Equal("lesson.3", next!.Lesson.Id);            // L1 is untouched but L3 is the one under way
    }

    [Fact]
    public void There_is_no_next_lesson_once_everything_is_finished()
    {
        foreach (var l in new[] { L1, L2, L3 })
            foreach (var step in l.Steps) Recorder.CompleteStep(l, step.Id);

        Assert.Null(Today().NextLesson);
    }

    [Fact]
    public void Locked_lessons_are_never_suggested()
    {
        // only L1 and L3 can start; L2 waits for L1
        foreach (var step in L3.Steps) Recorder.CompleteStep(L3, step.Id);

        Assert.Equal("lesson.1", Today().NextLesson!.Lesson.Id);
    }

    [Fact]
    public void Reviews_due_is_the_size_of_todays_queue()
    {
        foreach (var step in L1.Steps) Recorder.CompleteStep(L1, step.Id);

        Assert.Equal(1, Today().ReviewsDue);
    }

    // --- Session ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void There_is_no_session_until_one_is_started()
    {
        Assert.Null(Today().Session);
    }

    [Fact]
    public void A_started_session_is_active_with_its_lesson()
    {
        Recorder.StartSession("lesson.1");

        var s = Today().Session!;

        Assert.Equal(("lesson.1", Noon), (s.LessonId, s.StartedAt));
    }

    [Fact]
    public void A_session_may_have_no_lesson()
    {
        Recorder.StartSession(null);

        Assert.Null(Today().Session!.LessonId);
    }

    [Fact]
    public void A_completed_session_is_no_longer_active()
    {
        Recorder.StartSession("lesson.1");
        _clock.Now = Noon.AddMinutes(20);
        Recorder.CompleteSession();

        Assert.Null(Today(Noon.AddMinutes(21)).Session);
    }

    [Fact]
    public void A_session_started_after_a_completed_one_is_the_active_one()
    {
        Recorder.StartSession("lesson.1");
        _clock.Now = Noon.AddMinutes(20);
        Recorder.CompleteSession();
        _clock.Now = Noon.AddMinutes(60);
        Recorder.StartSession("lesson.2");

        Assert.Equal("lesson.2", Today(Noon.AddMinutes(61)).Session!.LessonId);
    }

    [Fact]
    public void An_unfinished_session_from_an_earlier_day_has_lapsed()
    {
        _clock.Now = Noon.AddDays(-1);
        Recorder.StartSession("lesson.1");
        _clock.Now = Noon;

        Assert.Null(Today().Session);
    }

    // --- Session summary ----------------------------------------------------------------------------------------------------

    private SessionSummary Summary(DateTimeOffset since, DateTimeOffset until) =>
        LearnerStateProjector.Summarize(_log.ReadAll(), Content, since, until);

    [Fact]
    public void The_summary_counts_steps_reviews_and_accuracy_since_the_session_began()
    {
        foreach (var step in L1.Steps) Recorder.CompleteStep(L1, step.Id);                     // before the session: not counted
        _clock.Now = Noon.AddHours(1);
        var since = _clock.Now;
        Recorder.ReviewAnswered(L1.Id, L1.ReviewItems[0], true, 0, TimeSpan.FromSeconds(2), Rating.Good);
        Recorder.StepAnswered(L2.Id, "step.2.intro", false, [1]);
        Recorder.StepAnswered(L2.Id, "step.2.intro", true, [0]);
        Recorder.StepCompleted(L2.Id, "step.2.intro");
        Recorder.CodeSubmitted(L2.Id, "step.2.more", "x", 1, 1);
        Recorder.StepCompleted(L2.Id, "step.2.more");
        var until = Noon.AddHours(2);

        var s = Summary(since, until);

        Assert.Equal((2, 1, 4, 3), (s.Steps, s.Reviews, s.Attempts, s.Correct));
        Assert.Equal(0.75, s.Accuracy);
    }

    [Fact]
    public void Accuracy_is_unknown_when_nothing_was_attempted()
    {
        _clock.Now = Noon.AddHours(1);
        Recorder.StepCompleted(L2.Id, "step.2.intro");

        var s = Summary(Noon.AddMinutes(30), Noon.AddHours(2));

        Assert.Equal(0, s.Attempts);
        Assert.Null(s.Accuracy);
    }

    [Fact]
    public void An_empty_session_summarises_to_zeros_and_no_changes()
    {
        var s = Summary(Noon, Noon.AddMinutes(5));

        Assert.Equal((0, 0, 0, 0), (s.Steps, s.Reviews, s.Attempts, s.Correct));
        Assert.Empty(s.MasteryChanges);
    }

    [Fact]
    public void The_summary_reports_concepts_whose_mastery_moved_during_the_session()
    {
        foreach (var step in L1.Steps) Recorder.CompleteStep(L1, step.Id);
        _clock.Now = Noon.AddHours(1);
        var since = _clock.Now;
        Recorder.ReviewAnswered(L1.Id, L1.ReviewItems[0], true, 0, TimeSpan.FromSeconds(2), Rating.Good);

        var changes = Summary(since, Noon.AddHours(1).AddMinutes(5)).MasteryChanges;

        var c = Assert.Single(changes);
        Assert.Equal("concept.1", c.Concept.Id);
        Assert.Equal(0.0, c.Before);
        Assert.InRange(c.After, 0.1, 0.2);
        Assert.Equal(c.After - c.Before, c.Delta, 10);
        Assert.True(c.IsGain);
    }

    [Fact]
    public void A_concept_that_slips_is_reported_as_a_loss()
    {
        foreach (var step in L1.Steps) Recorder.CompleteStep(L1, step.Id);
        Recorder.ReviewAnswered(L1.Id, L1.ReviewItems[0], true, 0, TimeSpan.FromSeconds(2), Rating.Easy);
        var since = Noon.AddDays(30);                                                   // long after: it has faded
        _clock.Now = since;
        Recorder.ReviewAnswered(L1.Id, L1.ReviewItems[0], false, 0, TimeSpan.FromSeconds(2), Rating.Again);

        var c = Assert.Single(Summary(since, since.AddMinutes(1)).MasteryChanges);

        Assert.False(c.IsGain);
        Assert.True(c.After < c.Before);
    }

    [Fact]
    public void A_concept_that_only_faded_during_the_session_is_not_a_change()
    {
        foreach (var step in L1.Steps) Recorder.CompleteStep(L1, step.Id);
        Recorder.ReviewAnswered(L1.Id, L1.ReviewItems[0], true, 0, TimeSpan.FromSeconds(2), Rating.Good);
        _clock.Now = Noon.AddDays(10);
        Recorder.StepCompleted(L2.Id, "step.2.intro");                 // the session did something else entirely

        var s = Summary(Noon.AddDays(10).AddMinutes(-5), Noon.AddDays(10).AddMinutes(5));

        Assert.Empty(s.MasteryChanges);
    }

    [Fact]
    public void Mastery_changes_list_the_biggest_movers_first()
    {
        var two = new Lesson("lesson.m", "M", "track.a", [Explain("step.m.intro"), Choice("step.m.q1"), Choice("step.m.q2")])
        {
            Concepts = [new Concept("concept.m1", "M1"), new Concept("concept.m2", "M2")],
            ReviewItems = [new ReviewItem("review.m1", "concept.m1", "step.m.q1"), new ReviewItem("review.m2", "concept.m2", "step.m.q2")],
        };
        var content = new ContentGraph(Content.Manifest, [new Track("track.a", "A", [two.Id])], [two]);
        foreach (var step in two.Steps) Recorder.CompleteStep(two, step.Id);
        _clock.Now = Noon.AddHours(1);
        Recorder.ReviewAnswered(two.Id, two.ReviewItems[0], true, 0, TimeSpan.FromSeconds(2), Rating.Hard);
        Recorder.ReviewAnswered(two.Id, two.ReviewItems[1], true, 0, TimeSpan.FromSeconds(2), Rating.Easy);

        var changes = LearnerStateProjector.Summarize(_log.ReadAll(), content, Noon.AddHours(1), Noon.AddHours(2)).MasteryChanges;

        Assert.Equal(["concept.m2", "concept.m1"], changes.Select(c => c.Concept.Id));
    }
}
