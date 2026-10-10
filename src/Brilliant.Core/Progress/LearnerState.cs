using Brilliant.Core.Content;

namespace Brilliant.Core.Progress;

public enum LessonStatus { Locked, Available, InProgress, Completed }

public sealed record LessonState(Lesson Lesson, LessonStatus Status, LessonProgress Progress)
{
    public bool IsUnlocked => Status != LessonStatus.Locked;

    /// <summary>Steps added to the lesson after the learner finished it. The lesson stays Completed.</summary>
    public int NewStepCount => Progress.NewSteps.Count;
}

public sealed record TrackState(Track Track, IReadOnlyList<LessonState> Lessons)
{
    public int TotalLessons => Lessons.Count;
    public int CompletedLessons => Lessons.Count(l => l.Status == LessonStatus.Completed);
    public bool IsComplete => TotalLessons > 0 && CompletedLessons == TotalLessons;
}

/// <summary>Everything the app knows about the learner, derived from the event log. Never stored.</summary>
/// <param name="AsOf">The "now" the state was projected for.</param>
public sealed record LearnerState(DateTimeOffset AsOf, IReadOnlyList<TrackState> Tracks)
{
    public TrackState? Track(string trackId) => Tracks.FirstOrDefault(t => t.Track.Id == trackId);

    public LessonState? Lesson(string lessonId) =>
        Tracks.SelectMany(t => t.Lessons).FirstOrDefault(l => l.Lesson.Id == lessonId);
}
