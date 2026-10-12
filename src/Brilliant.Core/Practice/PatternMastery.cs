using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Practice;

/// <summary>
/// A problem step tagged with a pattern, in a completed lesson: what Practice can offer.
/// <see cref="Pick"/> and <see cref="Resolve"/> are its derived review items, null until they exist.
/// </summary>
/// <param name="LastPractised">When it was last answered in practice or an interview; null if never.</param>
/// <param name="Mastery">The mean of the two items' mastery (<see cref="MasteryModel.OfPracticeStep"/>).</param>
public sealed record PatternStep(LessonState Lesson, Step Step, ReviewItemState? Pick, ReviewItemState? Resolve, DateTimeOffset? LastPractised,
    double Mastery)
{
    public bool IsPractised => LastPractised is not null;

    /// <summary>Its pick or re-solve was last rated Again (wrong, or right only once the answer was shown), or its re-solve is due.</summary>
    public bool NeedsWork(DateTimeOffset now) =>
        Pick?.LastRating == Rating.Again || Resolve?.LastRating == Rating.Again || Resolve?.IsDue(now) == true;
}

/// <summary>
/// How well the learner recognises and applies one pattern. Separate from concept mastery: practice never moves a concept's bar.
/// </summary>
/// <param name="Lesson">The lesson that teaches (declares) the pattern.</param>
/// <param name="Mastery">The mean of <see cref="Steps"/>' mastery, so an unpractised problem counts as zero; zero until <see cref="IsLearned"/>.</param>
/// <param name="Steps">The problems tagged with the pattern in completed lessons of any track, in content order.</param>
public sealed record PatternMastery(Pattern Pattern, LessonState Lesson, double Mastery, ConceptLevel Level, IReadOnlyList<PatternStep> Steps)
{
    /// <summary>The lesson that teaches it is completed, so it can be offered as a choice and its problems practised.</summary>
    public bool IsLearned => Lesson.Status == LessonStatus.Completed;

    public int StepsTagged => Steps.Count;
    public int StepsPractised => Steps.Count(s => s.IsPractised);
}
