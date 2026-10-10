using Brilliant.Core.Content;

namespace Brilliant.Core.Review;

/// <summary>
/// Turns what the learner did into an FSRS rating, so they never grade themselves. Pure.
/// Wrong → Again. Correct but shown the partial code or solution (3+ hints) → Again. Correct after a nudge (1–2 hints) → Hard.
/// Otherwise the time decides: at or under the step type's "fast" threshold → Easy, at or over "slow" → Hard, else Good.
/// </summary>
public static class RatingInference
{
    /// <summary>The hint-ladder rung from which the learner has effectively been shown the answer (nudge, pattern, partial code, solution).</summary>
    public const int ShownTheAnswerHints = 3;

    public static Rating Infer(bool correct, int hintsUsed, TimeSpan elapsed, Step step)
    {
        if (!correct || hintsUsed >= ShownTheAnswerHints) return Rating.Again;
        if (hintsUsed > 0) return Rating.Hard;
        var (fast, slow) = Thresholds(step);
        return elapsed <= fast ? Rating.Easy : elapsed >= slow ? Rating.Hard : Rating.Good;
    }

    /// <summary>How long a typical recall takes for this kind of step: quicker than <c>Fast</c> is Easy, slower than <c>Slow</c> is Hard.</summary>
    public static (TimeSpan Fast, TimeSpan Slow) Thresholds(Step step) => step switch
    {
        ChoiceStep => (TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(30)),
        PredictOutputStep => (TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)),
        FillBlankStep => (TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(90)),
        ParsonsStep => (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(150)),
        WriteCodeStep => (TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(420)),
        _ => (TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)),
    };
}
