using System.Globalization;

namespace Brilliant.Core.Content;

/// <summary>
/// The grade of a design-tradeoff answer. <see cref="Score"/> is 0 to 1. <see cref="Coverage"/> is the share of rubric weight the learner
/// ticked. <see cref="IsCorrect"/> is what Review scheduling and the lesson test-out see: the right option, enough of the rubric, and no
/// red flag claimed.
/// </summary>
public sealed record DesignTradeoffResult(
    bool OptionCorrect,
    string? OptionFeedback,
    int PointsEarned,
    int PointsTotal,
    double Coverage,
    int RedFlagsClaimed,
    double Score,
    bool IsCorrect,
    string Summary);

/// <summary>
/// Grades a design-tradeoff answer. Pure: the same step, option and ticks always give the same result.
/// The option is worth 0.4 and the justification 0.6 times the rubric coverage; each red flag the learner says their answer claimed costs 0.1.
/// An answer is correct when the option is right, at least 60% of the rubric weight was made, and no red flag was claimed.
/// </summary>
public static class DesignTradeoffEvaluator
{
    public const double OptionWeight = 0.4;
    public const double JustificationWeight = 0.6;
    public const double RedFlagPenalty = 0.1;
    public const double PassCoverage = 0.6;

    public static DesignTradeoffResult Evaluate(DesignTradeoffStep step, int? selectedOption, IEnumerable<int> pointsMade, IEnumerable<int> redFlagsClaimed)
    {
        var chosen = selectedOption is { } i && i >= 0 && i < step.Options.Count ? step.Options[i] : null;
        var optionCorrect = chosen?.Correct == true;

        var made = pointsMade.Where(p => p >= 0 && p < step.Rubric.Count).Distinct().ToList();
        var earned = made.Sum(p => step.Rubric[p].Points);
        var total = step.Rubric.Sum(p => p.Points);
        var coverage = total == 0 ? 0 : (double)earned / total;
        var flags = redFlagsClaimed.Where(f => f >= 0 && f < step.RedFlags.Count).Distinct().Count();

        var score = Math.Clamp((optionCorrect ? OptionWeight : 0) + JustificationWeight * coverage - RedFlagPenalty * flags, 0, 1);
        var isCorrect = optionCorrect && coverage >= PassCoverage && flags == 0;
        return new DesignTradeoffResult(optionCorrect, chosen?.Feedback, earned, total, coverage, flags, score, isCorrect,
            Summarise(optionCorrect, made.Count, step.Rubric.Count, flags));
    }

    private static string Summarise(bool optionCorrect, int made, int rubric, int flags)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{(optionCorrect ? "You picked the right option" : "That was not the best option")}, and your justification made {made} of {rubric} {(rubric == 1 ? "point" : "points")}");
        if (flags > 0) text += $", with {flags} red {(flags == 1 ? "flag" : "flags")}";
        return text + ".";
    }
}
