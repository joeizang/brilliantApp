using Brilliant.Core.Content;

namespace Brilliant.Core.Progress;

/// <summary>Where the learner is in a lesson, derived purely from the event log.</summary>
public sealed record LessonProgress(Lesson Lesson, IReadOnlySet<string> CompletedStepIds, bool CompletionRecorded = false)
{
    public int TotalSteps => Lesson.Steps.Count;
    public int CompletedCount => Lesson.Steps.Count(s => CompletedStepIds.Contains(s.Id));
    public int Remaining => TotalSteps - CompletedCount;

    /// <summary>
    /// Complete once every step is done, and stays complete after a LessonCompleted event even if the
    /// content later gains steps; replaying a completed lesson never changes this.
    /// </summary>
    public bool IsComplete => CompletionRecorded || Remaining == 0;

    /// <summary>The first step without a StepCompleted event (progress attaches to IDs, so reordering is safe); null when done.</summary>
    public Step? CurrentStep => IsComplete ? null : Lesson.Steps.FirstOrDefault(s => !CompletedStepIds.Contains(s.Id));

    public static LessonProgress From(Lesson lesson, IEnumerable<ProgressEvent> events)
    {
        var ofLesson = events.Where(e => e.LessonId == lesson.Id).ToList();
        return new(lesson,
            ofLesson.Where(e => e.Type == ProgressEventTypes.StepCompleted).Select(e => e.StepId).ToHashSet(),
            ofLesson.Any(e => e.Type == ProgressEventTypes.LessonCompleted));
    }
}
