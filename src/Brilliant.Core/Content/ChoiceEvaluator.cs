namespace Brilliant.Core.Content;

public sealed record ChoiceResult(bool IsCorrect, IReadOnlyList<int> Selected);

public static class ChoiceEvaluator
{
    /// <summary>Correct when the selected option indexes are exactly the set of correct options.</summary>
    public static ChoiceResult Evaluate(ChoiceStep step, IEnumerable<int> selected)
    {
        var chosen = selected.Distinct().Order().ToList();
        if (chosen.Any(i => i < 0 || i >= step.Options.Count))
            throw new ArgumentOutOfRangeException(nameof(selected), "Selected option index is out of range.");

        var correct = step.Options.Select((o, i) => (o, i)).Where(t => t.o.Correct).Select(t => t.i);
        return new ChoiceResult(chosen.SequenceEqual(correct), chosen);
    }
}
