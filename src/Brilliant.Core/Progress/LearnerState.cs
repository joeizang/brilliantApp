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
/// <param name="AttemptHints">The highest hint rung asked in Review for this item since it was last answered: help already taken on the current attempt.</param>
/// <param name="LessonHints">The highest hint rung the learner used on the question step while learning it in the lesson.</param>
public sealed record ReviewItemState(Lesson Lesson, ReviewItem Item, Step Question, bool IsUnlocked, CardState? Card, int Answers,
    int AttemptHints = 0, int LessonHints = 0)
{
    public bool IsNew => Card is null;

    /// <summary>
    /// Lesson help that counts against the first review: someone who needed the answer shown while learning it hasn't yet proved they own it.
    /// A re-solve is exempt (it exists to test that very problem again), as is anything already reviewed.
    /// </summary>
    public int CarriedHints => Item.Kind != ReviewKind.Resolve && Answers == 0 ? LessonHints : 0;

    /// <summary>The hints to count against the rating of this attempt so far, given the help already taken in Review.</summary>
    public int HintsBefore(int askedThisAttempt) => Math.Max(Math.Max(AttemptHints, askedThisAttempt), CarriedHints);

    public bool IsDue(DateTimeOffset now) => IsUnlocked && (Card is null || Card.Due <= now);
}

/// <summary>Everything the app knows about the learner, derived from the event log. Never stored.</summary>
/// <param name="AsOf">The "now" the state was projected for.</param>
/// <param name="Reviews">Every review item of the current content, in track and lesson order, re-solves of problems answered incorrectly included.</param>
/// <param name="Queue">What Review offers today: the due items, capped and ordered by the Review Queue Builder.</param>
/// <param name="Concepts">Mastery of every concept of the current content, in track and lesson order.</param>
/// <param name="Today">The daily goal, streak, next lesson and session, for the Today screen.</param>
public sealed record LearnerState(DateTimeOffset AsOf, IReadOnlyList<TrackState> Tracks, IReadOnlyList<ReviewItemState> Reviews, ReviewQueue Queue,
    IReadOnlyList<ConceptMastery> Concepts, TodayState Today)
{
    public TrackState? Track(string trackId) => Tracks.FirstOrDefault(t => t.Track.Id == trackId);

    public LessonState? Lesson(string lessonId) =>
        Tracks.SelectMany(t => t.Lessons).FirstOrDefault(l => l.Lesson.Id == lessonId);

    public ConceptMastery? Concept(string conceptId) => Concepts.FirstOrDefault(c => c.Concept.Id == conceptId);

    public IEnumerable<ConceptMastery> ConceptsOf(string trackId) => Concepts.Where(c => c.Lesson.Lesson.TrackId == trackId);

    /// <summary>Mean mastery (0–1) of a track's concepts; concepts not yet reached count as zero. Zero for an unknown or concept-less track.</summary>
    public double TrackMastery(string trackId)
    {
        var concepts = ConceptsOf(trackId).ToList();
        return concepts.Count == 0 ? 0 : concepts.Average(c => c.Mastery);
    }
}
