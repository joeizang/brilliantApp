using Brilliant.Core.Content;
using Brilliant.Core.Review;

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

/// <summary>
/// A review item as the learner currently stands with it. <see cref="Card"/> is null until it has been answered once.
/// Items are locked until their lesson is completed.
/// </summary>
/// <param name="Question">The answerable step the item re-asks.</param>
public sealed record ReviewItemState(Lesson Lesson, ReviewItem Item, Step Question, bool IsUnlocked, CardState? Card, int Answers)
{
    public bool IsNew => Card is null;

    public bool IsDue(DateTimeOffset now) => IsUnlocked && (Card is null || Card.Due <= now);
}

/// <summary>Everything the app knows about the learner, derived from the event log. Never stored.</summary>
/// <param name="AsOf">The "now" the state was projected for.</param>
/// <param name="Reviews">Every review item of the current content, in track and lesson order, re-solves of problems answered incorrectly included.</param>
/// <param name="Queue">What Review offers today: the due items, capped and ordered by the Review Queue Builder.</param>
public sealed record LearnerState(DateTimeOffset AsOf, IReadOnlyList<TrackState> Tracks, IReadOnlyList<ReviewItemState> Reviews, ReviewQueue Queue)
{
    public TrackState? Track(string trackId) => Tracks.FirstOrDefault(t => t.Track.Id == trackId);

    public LessonState? Lesson(string lessonId) =>
        Tracks.SelectMany(t => t.Lessons).FirstOrDefault(l => l.Lesson.Id == lessonId);
}
