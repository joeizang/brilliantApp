using Brilliant.Core.Content;
using Brilliant.Core.Review;

namespace Brilliant.Core.Progress;

/// <summary>How far along a learner is with a concept. Only <see cref="Known"/> and <see cref="Learning"/> have a mastery above zero.</summary>
public enum ConceptLevel
{
    /// <summary>The lesson that teaches the concept isn't finished yet.</summary>
    NotStarted,

    /// <summary>The lesson is finished but no review has proved anything: "merely seen".</summary>
    Seen,

    /// <summary>Reviewed, and still firming up (or slipping).</summary>
    Learning,

    /// <summary>Mastery at or above <see cref="MasteryModel.KnownThreshold"/>.</summary>
    Known,
}

public enum AttemptSource { Lesson, Review }

/// <summary>One answer to a question that exercises a concept, in the lesson that taught it or in Review.</summary>
public sealed record ConceptAttempt(DateTimeOffset At, AttemptSource Source, string StepId, bool Correct);

/// <summary>
/// What the learner knows of one concept. <see cref="Items"/> are the review items that test it (re-solves belong to no concept);
/// <see cref="History"/> is every answer to their questions, newest first.
/// </summary>
public sealed record ConceptMastery(
    Concept Concept, LessonState Lesson, double Mastery, ConceptLevel Level,
    IReadOnlyList<ReviewItemState> Items, IReadOnlyList<ConceptAttempt> History)
{
    public int LessonAttempts => History.Count(h => h.Source == AttemptSource.Lesson);
    public int LessonCorrect => History.Count(h => h.Source == AttemptSource.Lesson && h.Correct);
    public int ReviewAttempts => History.Count(h => h.Source == AttemptSource.Review);
    public int ReviewCorrect => History.Count(h => h.Source == AttemptSource.Review && h.Correct);

    /// <summary>Share (0–1) of all attempts, in lessons and in Review, that were right; null until there is one.</summary>
    public double? Accuracy => History.Count == 0 ? null : (double)History.Count(h => h.Correct) / History.Count;

    /// <summary>The soonest a review of this concept falls due, or null if none has been answered yet.</summary>
    public DateTimeOffset? NextDue => Items.Where(i => i.Card is not null).Select(i => (DateTimeOffset?)i.Card!.Due).Min();
}

/// <summary>
/// The mastery rule, in one place. A review item's mastery is how long the memory is predicted to last
/// (FSRS stability, in units of <see cref="KnownStabilityDays"/>) times how much of it is predicted to remain right now
/// (retrievability). A concept averages its items, so an item never reviewed counts as zero; a concept with no items can
/// be seen but never mastered. A track averages its concepts, so concepts not yet reached count as zero.
/// </summary>
public static class MasteryModel
{
    /// <summary>A memory that lasts this long (stability, in days) is treated as fully learned.</summary>
    public const double KnownStabilityDays = 21;

    /// <summary>Mastery at or above this is "known".</summary>
    public const double KnownThreshold = 0.8;

    public static double OfItem(CardState? card, DateTimeOffset now) =>
        card is null ? 0 : Math.Min(1, card.Stability / KnownStabilityDays) * FsrsScheduler.Retrievability(card, now);

    public static double OfConcept(IReadOnlyList<ReviewItemState> items, DateTimeOffset now) =>
        items.Count == 0 ? 0 : items.Average(i => OfItem(i.Card, now));

    public static ConceptLevel LevelOf(bool lessonCompleted, IReadOnlyList<ReviewItemState> items, double mastery) =>
        !lessonCompleted ? ConceptLevel.NotStarted
        : mastery >= KnownThreshold ? ConceptLevel.Known
        : items.Any(i => i.Card is not null) ? ConceptLevel.Learning
        : ConceptLevel.Seen;
}
