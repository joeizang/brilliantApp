using Brilliant.Core.Content;

namespace Brilliant.Core.Progress;

/// <summary>Where the learner is in a lesson, derived purely from the event log.</summary>
public sealed record LessonProgress(Lesson Lesson, IReadOnlySet<string> CompletedStepIds)
{
    public int TotalSteps => Lesson.Steps.Count;
    public int CompletedCount => Lesson.Steps.Count(s => CompletedStepIds.Contains(s.Id));
    public int Remaining => TotalSteps - CompletedCount;
    public bool IsComplete => Remaining == 0;

    /// <summary>The first step without a StepCompleted event (progress attaches to IDs, so reordering is safe); null when done.</summary>
    public Step? CurrentStep => Lesson.Steps.FirstOrDefault(s => !CompletedStepIds.Contains(s.Id));

    public static LessonProgress From(Lesson lesson, IEnumerable<ProgressEvent> events) =>
        new(lesson, events
            .Where(e => e.Type == ProgressEventTypes.StepCompleted && e.LessonId == lesson.Id)
            .Select(e => e.StepId)
            .ToHashSet());
}
