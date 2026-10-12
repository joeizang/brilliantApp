using Brilliant.Core.Practice;
using Brilliant.Core.Progress;

namespace Brilliant.Lessons.UI;

/// <summary>Words for Practice: why it isn't open yet, the weakest pattern, and when an answer comes back.</summary>
internal static class PracticeText
{
    /// <summary>The one line the disabled Practice card (and the Practice screen) gives for being closed.</summary>
    public static string Unavailable(PracticeAvailability availability) =>
        availability.PatternsLearned < PracticeSelector.MinimumPatterns
            ? $"Finish lessons that teach {PracticeSelector.MinimumPatterns} patterns to unlock Practice ({availability.PatternsLearned} so far)."
            : "None of the lessons you've finished has a problem to practise yet.";

    /// <summary>The learned pattern with the lowest mastery, e.g. "Prefix sums: learning, 34%". Null when none is learned.</summary>
    public static string? Weakest(LearnerState state) =>
        state.Patterns.Where(p => p.IsLearned).OrderBy(p => p.Mastery).FirstOrDefault() is { } weakest
            ? $"{weakest.Pattern.Title}: {ConceptText.Level(weakest.Level).ToLowerInvariant()}, {(int)Math.Round(100 * weakest.Mastery)}%"
            : null;

    /// <summary>How long until something is due, as Review says it: "within the hour", "in 5 hours", "in 3 days".</summary>
    public static string Describe(TimeSpan wait) =>
        wait < TimeSpan.FromHours(1) ? "within the hour"
        : wait < TimeSpan.FromDays(1) ? $"in {(int)wait.TotalHours} hours"
        : $"in {(int)Math.Round(wait.TotalDays)} {((int)Math.Round(wait.TotalDays) == 1 ? "day" : "days")}";
}
