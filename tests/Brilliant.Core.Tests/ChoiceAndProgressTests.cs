using Brilliant.Core.Content;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Tests;

public class ChoiceAndProgressTests
{
    private static readonly ChoiceStep Single = new("step.s", "S", "?", false,
        [new("a", false, null), new("b", true, "yes"), new("c", false, null)]);

    private static readonly ChoiceStep Multi = new("step.m", "M", "?", true,
        [new("a", true, null), new("b", false, null), new("c", true, null)]);

    [Theory]
    [InlineData(new[] { 1 }, true)]
    [InlineData(new[] { 0 }, false)]
    [InlineData(new[] { 0, 1 }, false)]
    [InlineData(new int[0], false)]
    public void Single_choice_is_correct_only_for_the_one_right_option(int[] picked, bool expected) =>
        Assert.Equal(expected, ChoiceEvaluator.Evaluate(Single, picked).IsCorrect);

    [Theory]
    [InlineData(new[] { 0, 2 }, true)]
    [InlineData(new[] { 2, 0 }, true)]
    [InlineData(new[] { 0, 2, 2 }, true)]
    [InlineData(new[] { 0 }, false)]
    [InlineData(new[] { 0, 1, 2 }, false)]
    public void Multi_select_needs_exactly_the_correct_set(int[] picked, bool expected) =>
        Assert.Equal(expected, ChoiceEvaluator.Evaluate(Multi, picked).IsCorrect);

    [Fact]
    public void Out_of_range_selection_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChoiceEvaluator.Evaluate(Single, [3]));

    private static readonly Lesson Lesson = new("lesson.l", "L", "track.t",
    [
        new ExplainStep("step.1", "1", "b", [], null),
        Single,
        new ExplainStep("step.3", "3", "b", [], null),
    ]);

    private static ProgressEvent Completed(string lesson, string step) =>
        new(Guid.NewGuid().ToString(), "d", DateTimeOffset.UnixEpoch, ProgressEventTypes.StepCompleted, lesson, step);

    [Fact]
    public void Fresh_lesson_starts_at_the_first_step()
    {
        var p = LessonProgress.From(Lesson, []);
        Assert.Equal("step.1", p.CurrentStep?.Id);
        Assert.Equal(3, p.Remaining);
        Assert.False(p.IsComplete);
    }

    [Fact]
    public void Resumes_at_the_first_uncompleted_step_and_counts_remaining()
    {
        var p = LessonProgress.From(Lesson, [Completed("lesson.l", "step.1"), Completed("lesson.l", "step.s")]);
        Assert.Equal("step.3", p.CurrentStep?.Id);
        Assert.Equal(1, p.Remaining);
    }

    [Fact]
    public void Answered_but_not_completed_and_other_lessons_events_do_not_count()
    {
        var answered = new ProgressEvent("x", "d", DateTimeOffset.UnixEpoch, ProgressEventTypes.StepAnswered, "lesson.l", "step.1");
        var p = LessonProgress.From(Lesson, [answered, Completed("lesson.other", "step.1")]);
        Assert.Equal("step.1", p.CurrentStep?.Id);
    }

    [Fact]
    public void Completing_every_step_completes_the_lesson()
    {
        var p = LessonProgress.From(Lesson, Lesson.Steps.Select(s => Completed("lesson.l", s.Id)));
        Assert.True(p.IsComplete);
        Assert.Null(p.CurrentStep);
        Assert.Equal(0, p.Remaining);
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    [Fact]
    public void Recorder_stamps_unique_ids_device_and_time()
    {
        var log = new MemoryLog();
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var recorder = new ProgressRecorder(log, "dev-9", new FixedClock(now));

        recorder.StepAnswered("lesson.l", "step.s", true, [1]);
        recorder.StepCompleted("lesson.l", "step.s");

        Assert.Equal([ProgressEventTypes.StepAnswered, ProgressEventTypes.StepCompleted], log.Items.Select(e => e.Type));
        Assert.All(log.Items, e => { Assert.Equal("dev-9", e.DeviceId); Assert.Equal(now, e.OccurredAt); });
        Assert.NotEqual(log.Items[0].Id, log.Items[1].Id);
        Assert.Contains("\"correct\":true", log.Items[0].Data);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
