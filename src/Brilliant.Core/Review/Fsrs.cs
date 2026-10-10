namespace Brilliant.Core.Review;

/// <summary>How well the learner recalled an item. The numeric values are the FSRS grades and are what is stored in the event log.</summary>
public enum Rating { Again = 1, Hard = 2, Good = 3, Easy = 4 }

/// <summary>
/// An item's memory as FSRS models it. <see cref="Stability"/> is the number of days until recall drops to 90%;
/// <see cref="Difficulty"/> runs from 1 (easy) to 10 (hard).
/// </summary>
public sealed record CardState(double Stability, double Difficulty, DateTimeOffset LastReview, DateTimeOffset Due, int Reps, int Lapses);

/// <summary>
/// FSRS-5 with its published default parameters and 90% desired retention; no per-learner optimisation in v1.
/// Pure: the next state depends only on the arguments, so schedules can always be recomputed from the event log.
/// </summary>
public static class FsrsScheduler
{
    public const double DesiredRetention = 0.9;
    public const int MaxIntervalDays = 36500;

    private static readonly double[] W =
    [
        0.40255, 1.18385, 3.173, 15.69105, 7.1949, 0.5345, 1.4604, 0.0046, 1.54575, 0.1192,
        1.01925, 1.9395, 0.11, 0.29605, 2.2698, 0.2315, 2.9898, 0.51655, 0.6621,
    ];

    private const double Decay = -0.5;
    private const double Factor = 19.0 / 81.0;

    /// <summary>The state after reviewing a card (or seeing a new item, when <paramref name="card"/> is null) at <paramref name="now"/>.</summary>
    public static CardState Schedule(CardState? card, Rating rating, DateTimeOffset now)
    {
        var grade = (int)rating;
        if (card is null)
            return Next(W[grade - 1], InitialDifficulty(grade), now, rating, reps: 1, lapses: grade == 1 ? 1 : 0);

        var elapsedDays = Math.Max(0, (now - card.LastReview).TotalDays);
        var difficulty = NextDifficulty(card.Difficulty, grade);
        var stability = elapsedDays < 1 ? ShortTermStability(card.Stability, grade)
            : rating == Rating.Again ? StabilityAfterLapse(card, elapsedDays)
            : StabilityAfterRecall(card, elapsedDays, rating);
        return Next(stability, difficulty, now, rating, card.Reps + 1, card.Lapses + (rating == Rating.Again ? 1 : 0));
    }

    /// <summary>The chance (0–1) the learner can still recall the item at <paramref name="now"/>.</summary>
    public static double Retrievability(CardState card, DateTimeOffset now) =>
        Retrievability(Math.Max(0, (now - card.LastReview).TotalDays), card.Stability);

    private static CardState Next(double stability, double difficulty, DateTimeOffset now, Rating rating, int reps, int lapses)
    {
        var days = Math.Clamp(Math.Round(stability / Factor * (Math.Pow(DesiredRetention, 1 / Decay) - 1), MidpointRounding.AwayFromZero), 1, MaxIntervalDays);
        return new CardState(stability, difficulty, now, now.AddDays(days), reps, lapses);
    }

    private static double Retrievability(double elapsedDays, double stability) => Math.Pow(1 + Factor * elapsedDays / stability, Decay);

    private static double ClampDifficulty(double d) => Math.Clamp(d, 1, 10);

    private static double InitialDifficulty(int grade) => ClampDifficulty(W[4] - Math.Exp(W[5] * (grade - 1)) + 1);

    // Linear damping (changes shrink near 10) and mean reversion toward the difficulty of a first "Easy".
    private static double NextDifficulty(double d, int grade)
    {
        var damped = d + -W[6] * (grade - 3) * (10 - d) / 9;
        return ClampDifficulty(W[7] * InitialDifficulty(4) + (1 - W[7]) * damped);
    }

    private static double StabilityAfterRecall(CardState card, double elapsedDays, Rating rating)
    {
        var r = Retrievability(elapsedDays, card.Stability);
        var hardPenalty = rating == Rating.Hard ? W[15] : 1;
        var easyBonus = rating == Rating.Easy ? W[16] : 1;
        return card.Stability * (1 + Math.Exp(W[8]) * (11 - card.Difficulty) * Math.Pow(card.Stability, -W[9])
            * (Math.Exp(W[10] * (1 - r)) - 1) * hardPenalty * easyBonus);
    }

    private static double StabilityAfterLapse(CardState card, double elapsedDays)
    {
        var r = Retrievability(elapsedDays, card.Stability);
        var forgotten = W[11] * Math.Pow(card.Difficulty, -W[12]) * (Math.Pow(card.Stability + 1, W[13]) - 1) * Math.Exp(W[14] * (1 - r));
        return Math.Min(forgotten, card.Stability);
    }

    private static double ShortTermStability(double stability, int grade) => stability * Math.Exp(W[17] * (grade - 3 + W[18]));
}
