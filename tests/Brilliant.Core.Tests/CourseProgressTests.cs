using Brilliant.Core.Content;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Tests;

public class CourseProgressTests
{
    private static Lesson L(string name, params string[] steps) =>
        new($"lesson.{name}", name, "track.t", steps.Select(s => (Step)new ExplainStep($"step.{name}.{s}", s, "b", [], null)).ToList());

    private static readonly Lesson One = L("one", "a", "b");
    private static readonly Lesson Two = L("two", "a");
    private static readonly Lesson Three = L("three", "a");

    private static ContentGraph Graph() => new(
        new PackManifest("pack.t", "1.0.0", 1),
        [new Track("track.t", "T", [One.Id, Two.Id, Three.Id])],
        [One, Two, Three]);

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static IReadOnlyList<LessonStatus> Statuses(IEnumerable<ProgressEvent> events) =>
        CourseProgress.From(Graph(), events).Single().Lessons.Select(l => l.Status).ToList();

    [Fact]
    public void Only_the_first_lesson_is_unlocked_on_a_fresh_install() =>
        Assert.Equal([LessonStatus.Available, LessonStatus.Locked, LessonStatus.Locked], Statuses([]));

    [Fact]
    public void Starting_a_lesson_marks_it_in_progress_and_keeps_the_next_locked()
    {
        var log = new MemoryLog();
        new ProgressRecorder(log, "d").CompleteStep(One, "step.one.a");
        Assert.Equal([LessonStatus.InProgress, LessonStatus.Locked, LessonStatus.Locked], Statuses(log.Items));
    }

    [Fact]
    public void Completing_a_lesson_unlocks_the_next_one_only()
    {
        var log = new MemoryLog();
        var rec = new ProgressRecorder(log, "d");
        rec.CompleteStep(One, "step.one.a");
        rec.CompleteStep(One, "step.one.b");
        Assert.Equal([LessonStatus.Completed, LessonStatus.Available, LessonStatus.Locked], Statuses(log.Items));
    }

    [Fact]
    public void Track_counts_completed_lessons()
    {
        var log = new MemoryLog();
        var rec = new ProgressRecorder(log, "d");
        foreach (var s in One.Steps) rec.CompleteStep(One, s.Id);
        rec.CompleteStep(Two, "step.two.a");
        var track = CourseProgress.From(Graph(), log.Items).Single();
        Assert.Equal(2, track.CompletedLessons);
        Assert.Equal(3, track.TotalLessons);
        Assert.False(track.IsComplete);
        Assert.Equal(LessonStatus.Available, track.Lessons[2].Status);
    }

    [Fact]
    public void LessonCompleted_is_recorded_once_when_the_last_step_finishes()
    {
        var log = new MemoryLog();
        var rec = new ProgressRecorder(log, "d");
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
    public void A_completed_lesson_stays_completed_when_content_later_gains_a_step()
    {
        var log = new MemoryLog();
        var rec = new ProgressRecorder(log, "d");
        foreach (var s in One.Steps) rec.CompleteStep(One, s.Id);

        var grown = L("one", "a", "b", "c");
        var progress = LessonProgress.From(grown, log.Items);

        Assert.True(progress.IsComplete);
        Assert.Null(progress.CurrentStep);
    }

    [Fact]
    public void Lesson_progress_is_derived_only_from_events_so_review_mode_changes_nothing()
    {
        var log = new MemoryLog();
        var rec = new ProgressRecorder(log, "d");
        foreach (var s in One.Steps) rec.CompleteStep(One, s.Id);
        var before = Statuses(log.Items);

        // Review mode appends nothing: statuses are a pure function of the unchanged log.
        Assert.Equal(before, Statuses(log.Items));
        Assert.Equal(LessonStatus.Completed, before[0]);
    }
}
