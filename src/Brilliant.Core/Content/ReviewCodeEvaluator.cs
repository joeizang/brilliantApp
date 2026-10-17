using System.Globalization;

namespace Brilliant.Core.Content;

/// <summary>A line the learner flagged, and the category they gave it. Lines count from 1.</summary>
public sealed record ReviewFlag(int Line, string Category);

public enum ReviewDefectOutcome { Found, WrongCategory, Missed }

/// <summary>How one authored defect fared. <see cref="Credit"/> is 1, 0.5 or 0; <see cref="Feedback"/> is shown whether or not it was found.</summary>
public sealed record ReviewDefectResult(ReviewDefect Defect, ReviewDefectOutcome Outcome, double Credit, string Feedback);

/// <summary>A flag on a line with no defect. <see cref="Feedback"/> is the decoy's note when the author wrote one.</summary>
public sealed record ReviewFalseFlag(int Line, string Category, string Feedback);

/// <summary>
/// The grade of a review. <see cref="Score"/> is 0 to 1; <see cref="IsCorrect"/> only for a perfect review (every defect found under its
/// category, nothing false flagged), which is what the learner's progress records.
/// </summary>
public sealed record ReviewCodeResult(
    double Score,
    bool IsCorrect,
    IReadOnlyList<ReviewDefectResult> Defects,
    IReadOnlyList<ReviewFalseFlag> FalseFlags,
    string Summary);

/// <summary>
/// Grades a review against the authored defects. Pure: the same step and flags always give the same result.
/// Full credit is a flag inside the defect's lines under its category, half credit is the right place under another category.
/// Each flagged clean line costs a quarter of a defect, and the score never goes below zero.
/// </summary>
public static class ReviewCodeEvaluator
{
    public const double FalseFlagPenalty = 0.25;
    public const double WrongCategoryCredit = 0.5;

    public static ReviewCodeResult Evaluate(ReviewCodeStep step, IReadOnlyList<ReviewFlag> flags)
    {
        var lineCount = step.Lines.Count;
        var valid = flags
            .Where(f => f.Line >= 1 && f.Line <= lineCount && ReviewCategory.All.Contains(f.Category))
            .Distinct()
            .ToList();

        var results = step.Defects
            .OrderBy(d => d.Line)
            .Select(d => Grade(d, valid))
            .ToList();

        var falseFlags = valid
            .Where(f => !step.Defects.Any(d => Covers(d, f.Line)))
            .GroupBy(f => f.Line)
            .Select(g => g.First())
            .OrderBy(f => f.Line)
            .Select(f => new ReviewFalseFlag(f.Line, f.Category, FalseFlagFeedback(step, f)))
            .ToList();

        var total = step.Defects.Count;
        var earned = results.Sum(r => r.Credit) - FalseFlagPenalty * falseFlags.Count;
        var score = total == 0 ? (falseFlags.Count == 0 ? 1 : 0) : Math.Clamp(earned / total, 0, 1);

        var found = results.Count(r => r.Outcome == ReviewDefectOutcome.Found);
        var isCorrect = found == total && falseFlags.Count == 0;
        return new ReviewCodeResult(score, isCorrect, results, falseFlags, Summarise(found, results, falseFlags.Count, total));
    }

    private static bool Covers(ReviewDefect defect, int line) => line >= defect.Line && line <= defect.EndLine;

    private static ReviewDefectResult Grade(ReviewDefect defect, IReadOnlyList<ReviewFlag> flags)
    {
        var inside = flags.Where(f => Covers(defect, f.Line)).ToList();
        var where = defect.Line == defect.EndLine ? $"line {defect.Line}" : $"lines {defect.Line} to {defect.EndLine}";
        if (inside.Count == 0)
            return new(defect, ReviewDefectOutcome.Missed, 0, $"Missed, at {where} ({defect.Category}): {defect.Explanation}");
        if (inside.Any(f => f.Category == defect.Category))
            return new(defect, ReviewDefectOutcome.Found, 1, $"Found, at {where} ({defect.Category}): {defect.Explanation}");
        var named = string.Join(" and ", inside.Select(f => f.Category).Distinct());
        return new(defect, ReviewDefectOutcome.WrongCategory, WrongCategoryCredit,
            $"Right place, wrong kind. You flagged {where} as {named}, but it is a {defect.Category} problem: {defect.Explanation}");
    }

    private static string FalseFlagFeedback(ReviewCodeStep step, ReviewFlag flag) =>
        step.Decoys.FirstOrDefault(d => d.Line == flag.Line) is { } decoy
            ? $"Line {flag.Line} is fine as written: {decoy.Explanation}"
            : $"Line {flag.Line} has nothing wrong with it.";

    private static string Summarise(int found, IReadOnlyList<ReviewDefectResult> results, int falseFlags, int total)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"You found {found} of {total} {(total == 1 ? "problem" : "problems")}");
        var wrongKind = results.Count(r => r.Outcome == ReviewDefectOutcome.WrongCategory);
        if (wrongKind > 0) text += $", {wrongKind} under the wrong kind";
        if (falseFlags > 0) text += $", and flagged {falseFlags} {(falseFlags == 1 ? "line" : "lines")} that {(falseFlags == 1 ? "is" : "are")} fine";
        return text + ".";
    }
}
