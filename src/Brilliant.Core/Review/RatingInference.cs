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

    /// <summary>
    /// Rates a pattern pick, in practice or as a <c>pick.</c> item in Review: wrong → Again, otherwise by time against
    /// <see cref="PickThresholds"/>, never by the problem's own format. Hints count as in <see cref="Infer"/>, though a pick offers none.
    /// </summary>
    public static Rating InferPick(bool correct, TimeSpan elapsed, int hintsUsed = 0) =>
        !correct || hintsUsed >= ShownTheAnswerHints ? Rating.Again
        : hintsUsed > 0 ? Rating.Hard
        : elapsed <= PickThresholds.Fast ? Rating.Easy
        : elapsed >= PickThresholds.Slow ? Rating.Hard
        : Rating.Good;

    /// <summary>
    /// Rates an answer given in a timed interview: the pick of a pattern or a submitted solution, made blind (no verdict until the end), with
    /// no hints and against the run's clock. Wrong → Again; at or under <paramref name="fast"/> → Easy; otherwise Good. Slowness is never
    /// Hard here: the clock already sets the pace, and a right answer under it is stronger evidence than the same answer at leisure.
    /// <paramref name="fast"/> is the part's own threshold (<see cref="PickThresholds"/> or the step type's <see cref="Thresholds"/>).
    /// </summary>
    public static Rating InferInterview(bool correct, TimeSpan elapsed, TimeSpan fast) =>
        !correct ? Rating.Again : elapsed <= fast ? Rating.Easy : Rating.Good;

    /// <summary>
    /// A pattern pick takes reading an unlabelled problem first, so it is given longer than a plain choice question: under 15 seconds is
    /// Easy, a minute or more is Hard.
    /// </summary>
    public static (TimeSpan Fast, TimeSpan Slow) PickThresholds { get; } = (TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60));

    /// <summary>How long a typical recall takes for this kind of step: quicker than <c>Fast</c> is Easy, slower than <c>Slow</c> is Hard.</summary>
    public static (TimeSpan Fast, TimeSpan Slow) Thresholds(Step step) => step switch
    {
        ChoiceStep => (TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(30)),
        PredictOutputStep => (TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)),
        // Reading generated code takes longer than predicting a printed value.
        LoweredStep => (TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(90)),
        // Reading a few implementations and weighing their costs takes longer than one prediction.
        BenchmarkPredictStep => (TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(90)),
        // Stepping two threads by hand to find the failing order is slower than reading a result.
        InterleaveStep => (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)),
        // Reading a screenful of code for several problems takes minutes, not seconds.
        ReviewCodeStep => (TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(240)),
        // Choosing and writing a justification takes a few minutes; ticking the rubric afterwards is quick.
        DesignTradeoffStep => (TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(300)),
        FillBlankStep => (TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(90)),
        ParsonsStep => (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(150)),
        WriteCodeStep => (TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(420)),
        _ => (TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)),
    };
}
