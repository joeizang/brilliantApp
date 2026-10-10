using Brilliant.Core.Content;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Tests;

public class LearnerStateProjectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static Lesson L(string name, params string[] steps) =>
        new($"lesson.{name}", name, "track.t", steps.Select(s => (Step)new ExplainStep($"step.{name}.{s}", s, "b", [], null)).ToList());

    private static readonly Lesson One = L("one", "a", "b");
    private static readonly Lesson Two = L("two", "a");
    private static readonly Lesson Three = L("three", "a");

    private static ContentGraph Graph(params Lesson[] lessons) => new(
        new PackManifest("pack.t", "1.0.0", 1),
        [new Track("track.t", "T", lessons.Select(l => l.Id).ToList())],
        lessons);

    private static ContentGraph Graph() => Graph(One, Two, Three);

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static (MemoryLog Log, ProgressRecorder Rec) NewLog()
    {
        var log = new MemoryLog();
        return (log, new ProgressRecorder(log, "d"));
    }

    private static LearnerState Project(IEnumerable<ProgressEvent> events, ContentGraph? content = null) =>
        LearnerStateProjector.Project(events, content ?? Graph(), Now);

    private static IReadOnlyList<LessonStatus> Statuses(IEnumerable<ProgressEvent> events, ContentGraph? content = null) =>
        Project(events, content).Tracks.Single().Lessons.Select(l => l.Status).ToList();

    /// <summary>Everything the UI can show, flattened so two projections can be compared by value.</summary>
    private static string Summary(LearnerState s) => string.Join("|", s.Tracks.SelectMany(t => t.Lessons).Select(l =>
        $"{l.Lesson.Id}:{l.Status}:{l.Progress.CompletedCount}/{l.Progress.TotalSteps}:new={l.Progress.NewSteps.Count}:retired={string.Join(",", l.Progress.RetiredStepIds.OrderBy(x => x))}:next={l.Progress.NextStep?.Id}"));

    // --- Lesson status and unlocking -------------------------------------------------------------------------------

    [Fact]
    public void Only_the_first_lesson_is_unlocked_on_a_fresh_install() =>
        Assert.Equal([LessonStatus.Available, LessonStatus.Locked, LessonStatus.Locked], Statuses([]));

    [Fact]
    public void Starting_a_lesson_marks_it_in_progress_and_keeps_the_next_locked()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(One, "step.one.a");
        Assert.Equal([LessonStatus.InProgress, LessonStatus.Locked, LessonStatus.Locked], Statuses(log.Items));
    }

    [Fact]
    public void Completing_a_lesson_unlocks_the_next_one_only()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(One, "step.one.a");
        rec.CompleteStep(One, "step.one.b");
        Assert.Equal([LessonStatus.Completed, LessonStatus.Available, LessonStatus.Locked], Statuses(log.Items));
    }

    [Fact]
    public void Track_counts_completed_lessons()
    {
        var (log, rec) = NewLog();
        foreach (var s in One.Steps) rec.CompleteStep(One, s.Id);
        rec.CompleteStep(Two, "step.two.a");
        var track = Project(log.Items).Tracks.Single();
        Assert.Equal(2, track.CompletedLessons);
        Assert.Equal(3, track.TotalLessons);
        Assert.False(track.IsComplete);
        Assert.Equal(LessonStatus.Available, track.Lessons[2].Status);
    }

    [Fact]
    public void LessonCompleted_is_recorded_once_when_the_last_step_finishes()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(One, "step.one.a");
        Assert.DoesNotContain(log.Items, e => e.Type == ProgressEventTypes.LessonCompleted);

        rec.CompleteStep(One, "step.one.b");
        var done = Assert.Single(log.Items, e => e.Type == ProgressEventTypes.LessonCompleted);
        Assert.Equal(One.Id, done.LessonId);
        Assert.Equal("", done.StepId);

        // Re-completing a step (e.g. a stray duplicate) must not emit a second LessonCompleted.
        rec.CompleteStep(One, "step.one.b");
        Assert.Single(log.Items, e => e.Type == ProgressEventTypes.LessonCompleted);
    }

    [Fact]
    public void Lookups_find_a_lesson_or_track_by_ID()
    {
        var state = Project([]);
        Assert.Equal(LessonStatus.Available, state.Lesson(One.Id)!.Status);
        Assert.Equal("T", state.Track("track.t")!.Track.Title);
        Assert.Null(state.Lesson("lesson.nope"));
        Assert.Null(state.Track("track.nope"));
    }

    // --- Determinism ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_same_events_and_content_always_project_to_the_same_state()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(One, "step.one.a");
        rec.CompleteStep(One, "step.one.b");
        rec.CompleteStep(Two, "step.two.a");

        Assert.Equal(Summary(Project(log.Items)), Summary(Project(log.Items)));
        Assert.Equal(Now, Project(log.Items).AsOf);
    }

    [Fact]
    public void The_order_events_arrive_in_does_not_change_the_state()
    {
        // After a sync merge, two devices' events interleave differently; both must reach identical state.
        var (log, rec) = NewLog();
        foreach (var s in One.Steps) rec.CompleteStep(One, s.Id);
        rec.CompleteStep(Two, "step.two.a");

        Assert.Equal(Summary(Project(log.Items)), Summary(Project(log.Items.AsEnumerable().Reverse())));
    }

    [Fact]
    public void An_event_delivered_twice_counts_once()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(One, "step.one.a");
        var doubled = log.Items.Concat(log.Items).ToList();

        Assert.Equal(Summary(Project(log.Items)), Summary(Project(doubled)));
        Assert.Equal(1, Project(doubled).Lesson(One.Id)!.Progress.CompletedCount);
    }

    [Fact]
    public void Projecting_never_changes_the_log_so_review_mode_changes_nothing()
    {
        var (log, rec) = NewLog();
        foreach (var s in One.Steps) rec.CompleteStep(One, s.Id);
        var before = log.Items.Count;

        var state = Project(log.Items);
        Project(log.Items);

        Assert.Equal(before, log.Items.Count);
        Assert.Equal(LessonStatus.Completed, state.Lesson(One.Id)!.Status);
    }

    // --- Content changes, handled by stable ID ----------------------------------------------------------------------

    [Fact]
    public void An_edited_step_keeps_its_history()
    {
        var (log, rec) = NewLog();
        foreach (var s in One.Steps) rec.CompleteStep(One, s.Id);

        var edited = new Lesson(One.Id, One.Title, One.TrackId,
            [new ExplainStep("step.one.a", "Better title", "Better body", [], null), One.Steps[1]]);
        var progress = Project(log.Items, Graph(edited, Two, Three)).Lesson(One.Id)!;

        Assert.Equal(LessonStatus.Completed, progress.Status);
        Assert.Equal(2, progress.Progress.CompletedCount);
        Assert.Empty(progress.Progress.NewSteps);
    }

    [Fact]
    public void Reordering_steps_keeps_progress_because_it_attaches_to_IDs()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(One, "step.one.a");

        var reordered = new Lesson(One.Id, One.Title, One.TrackId, [One.Steps[1], One.Steps[0]]);
        var progress = Project(log.Items, Graph(reordered, Two, Three)).Lesson(One.Id)!.Progress;

        Assert.Equal(1, progress.CompletedCount);
        Assert.Equal("step.one.b", progress.NextStep!.Id);
    }

    [Fact]
    public void A_deleted_step_retires_and_no_longer_counts_toward_the_lesson()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(One, "step.one.a");
        var trimmed = L("one", "b");   // step.one.a is gone

        var progress = Project(log.Items, Graph(trimmed, Two, Three)).Lesson(One.Id)!.Progress;

        Assert.Equal(0, progress.CompletedCount);
        Assert.Equal(1, progress.TotalSteps);
        Assert.Equal(["step.one.a"], progress.RetiredStepIds);
    }

    [Fact]
    public void Deleting_the_only_unfinished_step_completes_an_in_progress_lesson()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(One, "step.one.a");
        var trimmed = L("one", "a");   // step.one.b was never done and is gone

        var state = Project(log.Items, Graph(trimmed, Two, Three));

        Assert.Equal(LessonStatus.Completed, state.Lesson(One.Id)!.Status);
        Assert.Equal(LessonStatus.Available, state.Lesson(Two.Id)!.Status);
    }

    [Fact]
    public void Events_for_a_deleted_lesson_are_ignored()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(Two, "step.two.a");

        var state = Project(log.Items, Graph(One, Three));

        Assert.Equal([LessonStatus.Available, LessonStatus.Locked], state.Tracks.Single().Lessons.Select(l => l.Status));
    }

    [Fact]
    public void A_completed_lesson_that_gains_a_step_stays_completed_and_reports_it_as_new()
    {
        var (log, rec) = NewLog();
        foreach (var s in One.Steps) rec.CompleteStep(One, s.Id);
        var grown = L("one", "a", "b", "c");

        var state = Project(log.Items, Graph(grown, Two, Three));
        var lesson = state.Lesson(One.Id)!;

        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.True(lesson.Progress.IsComplete);
        Assert.Equal(["step.one.c"], lesson.Progress.NewSteps.Select(s => s.Id));
        Assert.Equal("step.one.c", lesson.Progress.NextStep!.Id);
        Assert.Null(lesson.Progress.CurrentStep);
        Assert.Equal(LessonStatus.Available, state.Lesson(Two.Id)!.Status);   // the next lesson stays unlocked
        Assert.Equal(1, state.Tracks.Single().CompletedLessons);
    }

    [Fact]
    public void Completing_a_new_step_clears_the_new_marker_without_a_second_LessonCompleted()
    {
        var (log, rec) = NewLog();
        foreach (var s in One.Steps) rec.CompleteStep(One, s.Id);
        var grown = L("one", "a", "b", "c");

        rec.CompleteStep(grown, "step.one.c");
        var lesson = Project(log.Items, Graph(grown, Two, Three)).Lesson(One.Id)!;

        Assert.Empty(lesson.Progress.NewSteps);
        Assert.Null(lesson.Progress.NextStep);
        Assert.Single(log.Items, e => e.Type == ProgressEventTypes.LessonCompleted);
    }

    [Fact]
    public void A_step_added_to_an_unfinished_lesson_is_just_another_remaining_step()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(One, "step.one.a");
        var grown = L("one", "a", "b", "c");

        var progress = Project(log.Items, Graph(grown, Two, Three)).Lesson(One.Id)!.Progress;

        Assert.Empty(progress.NewSteps);   // "new" only describes steps added after the lesson was finished
        Assert.Equal(2, progress.Remaining);
        Assert.Equal("step.one.b", progress.CurrentStep!.Id);
    }

    [Fact]
    public void A_single_lesson_can_be_projected_without_the_whole_course()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(One, "step.one.a");

        var progress = LearnerStateProjector.ProjectLesson(One, log.Items);

        Assert.Equal(1, progress.CompletedCount);
        Assert.Equal("step.one.b", progress.CurrentStep!.Id);
    }

    // --- Completion reached through a content deletion must survive later additions (review of #49) -----------------

    [Fact]
    public void Deleting_the_last_unfinished_step_then_adding_a_new_one_keeps_the_lesson_completed()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(One, "step.one.a");

        // Content update 1: step b is deleted, which completes the lesson. The app records that when it loads the pack.
        var trimmed = L("one", "a");
        Assert.Equal(1, rec.RecordCompletionsFrom(Graph(trimmed, Two, Three)));

        // Content update 2: step c is added. The lesson must stay completed with c flagged as new.
        var state = Project(log.Items, Graph(L("one", "a", "c"), Two, Three));
        var lesson = state.Lesson(One.Id)!;

        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.Equal(["step.one.c"], lesson.Progress.NewSteps.Select(x => x.Id));
        Assert.Equal(LessonStatus.Available, state.Lesson(Two.Id)!.Status);
    }

    [Fact]
    public void Recording_completions_is_idempotent_and_ignores_lessons_that_are_not_complete()
    {
        var (log, rec) = NewLog();
        rec.CompleteStep(One, "step.one.a");
        var content = Graph(L("one", "a"), Two, Three);

        Assert.Equal(1, rec.RecordCompletionsFrom(content));
        var after = log.Items.Count;
        Assert.Equal(0, rec.RecordCompletionsFrom(content));
        Assert.Equal(after, log.Items.Count);

        // Two and Three were never started, so nothing is recorded for them.
        Assert.Single(log.Items, e => e.Type == ProgressEventTypes.LessonCompleted);
    }

    [Fact]
    public void Recording_completions_skips_lessons_whose_completion_is_already_recorded()
    {
        var (log, rec) = NewLog();
        foreach (var s in One.Steps) rec.CompleteStep(One, s.Id);
        var before = log.Items.Count;

        Assert.Equal(0, rec.RecordCompletionsFrom(Graph()));
        Assert.Equal(before, log.Items.Count);
    }

    [Fact]
    public void An_empty_lesson_is_not_recorded_as_completed()
    {
        var (log, rec) = NewLog();
        var empty = new Lesson("lesson.empty", "Empty", "track.t", []);

        Assert.Equal(0, rec.RecordCompletionsFrom(Graph(empty)));
        Assert.Empty(log.Items);
    }
}
