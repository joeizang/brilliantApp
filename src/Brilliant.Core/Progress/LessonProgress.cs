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
    public Step? CurrentStep => IsComplete ? null : NextStep;

    /// <summary>The first step without a StepCompleted event, whether or not the lesson was finished before; null when every step is done.</summary>
    public Step? NextStep => Lesson.Steps.FirstOrDefault(s => !CompletedStepIds.Contains(s.Id));

    /// <summary>Steps added after the learner finished the lesson. Empty while the lesson is unfinished: then they are simply remaining.</summary>
    public IReadOnlyList<Step> NewSteps =>
        CompletionRecorded ? Lesson.Steps.Where(s => !CompletedStepIds.Contains(s.Id)).ToList() : [];

    /// <summary>Steps the learner completed that no longer exist in the content. Their events are kept but ignored.</summary>
    public IReadOnlyList<string> RetiredStepIds =>
        CompletedStepIds.Where(id => Lesson.Steps.All(s => s.Id != id)).Order(StringComparer.Ordinal).ToList();

    public static LessonProgress From(Lesson lesson, IEnumerable<ProgressEvent> events) =>
        LearnerStateProjector.ProjectLesson(lesson, events);
}
