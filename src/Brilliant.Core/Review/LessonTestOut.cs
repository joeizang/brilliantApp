using Brilliant.Core.Content;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Review;

/// <summary>
/// The optional "I know this, test me" at the start of a lesson (#146): 3–4 of the lesson's own review questions, the hardest ones. Every
/// answer right on the first try passes the test, and the lesson counts as done with its items already known; any miss ends the test and
/// the lesson starts as normal, with nothing recorded.
/// </summary>
public static class LessonTestOut
{
    /// <summary>The fewest askable review items a lesson needs to offer a test-out; a thinner lesson gives too little evidence.</summary>
    public const int MinQuestions = 3;

    public const int MaxQuestions = 4;

    /// <summary>
    /// The questions to ask, in the lesson's own order: the <see cref="MaxQuestions"/> hardest of its concept review items (see
    /// <see cref="Difficulty"/>), ties going to whichever comes first. Empty when the lesson has fewer than <see cref="MinQuestions"/> to
    /// ask. Pure and deterministic, so every device and every attempt asks the same questions.
    /// </summary>
    public static IReadOnlyList<ReviewItem> Select(Lesson lesson)
    {
        var steps = lesson.AllSteps().GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var askable = lesson.ReviewItems
            .Select((item, order) => (Item: item, Order: order, Rank: steps.TryGetValue(item.StepId, out var q) ? Difficulty(q) : 0))
            .Where(x => x.Item.Kind == ReviewKind.Concept && x.Rank > 0)
            .GroupBy(x => x.Item.StepId, StringComparer.Ordinal).Select(g => g.First())   // two items on one question are one question
            .ToList();
        if (askable.Count < MinQuestions) return [];
        return askable.OrderByDescending(x => x.Rank).ThenBy(x => x.Order).Take(MaxQuestions).OrderBy(x => x.Order).Select(x => x.Item).ToList();
    }

    /// <summary>
    /// Whether to offer the test: the lesson hasn't been started or finished, and has enough to ask. Once the learner has played any of it,
    /// the point of skipping ahead is gone.
    /// </summary>
    public static bool IsOffered(LessonProgress progress) => !progress.IsComplete && progress.CompletedCount == 0 && Select(progress.Lesson).Count > 0;

    /// <summary>How demanding a question is: recalling or producing code beats picking from options. 0 means it can't be asked here (an explanation, a trace).</summary>
    public static int Difficulty(Step step) => step switch
    {
        WriteCodeStep => 5,
        ParsonsStep => 4,
        FillBlankStep => 3,
        PredictOutputStep => 2,
        LoweredStep => 2,
        BenchmarkPredictStep => 2,
        // Finding the bad order is a puzzle, harder than picking from options but nothing like writing code.
        InterleaveStep => 3,
        ChoiceStep => 1,
        _ => 0,
    };
}
