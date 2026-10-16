using System.Text.RegularExpressions;

namespace Brilliant.Core.Content;

/// <summary>The verdict for one blank of a fill-in-the-blank step.</summary>
public sealed record BlankResult(string BlankId, bool IsCorrect, string? Feedback);

/// <summary>The verdict for a whole fill-in-the-blank step: correct only when every blank is.</summary>
public sealed record FillBlankResult(IReadOnlyList<BlankResult> Blanks)
{
    public bool IsCorrect => Blanks.All(b => b.IsCorrect);
}

/// <summary>Verdict for a non-code answer. <see cref="Feedback"/> is the authored message that applies, if any.</summary>
public sealed record AnswerResult(bool IsCorrect, string? Feedback);

/// <summary>Pure evaluation of predict-output responses. No I/O, no clock, no state.</summary>
public static class AnswerEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Makes equivalent outputs comparable: unifies line endings, straightens curly quotes, treats
    /// <c>"</c> and <c>'</c> alike, collapses runs of spaces/tabs, trims each line and trims the ends.
    /// Case and blank lines between content are preserved.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var unified = text
            .Replace("\r\n", "\n").Replace('\r', '\n')
            .Replace('‘', '\'').Replace('’', '\'')
            .Replace('“', '\'').Replace('”', '\'')
            .Replace('"', '\'');
        var lines = unified.Split('\n').Select(l => Regex.Replace(l, @"[ \t\f\v ]+", " ").Trim());
        return string.Join('\n', lines).Trim('\n');
    }

    /// <summary>Typed variant: correct if the normalised response equals a normalised accepted answer; otherwise the first matching mistake pattern supplies feedback.</summary>
    public static AnswerResult Evaluate(PredictOutputStep step, string? response)
    {
        if (!step.IsTyped) throw new InvalidOperationException($"Step '{step.Id}' is multiple-choice; pass the selected option index.");
        return Judge(step.Accepted, step.Mistakes, response);
    }

    /// <summary>Typed lowered step: judged like a typed prediction.</summary>
    public static AnswerResult Evaluate(LoweredStep step, string? response)
    {
        if (!step.IsTyped) throw new InvalidOperationException($"Step '{step.Id}' is multiple-choice; pass the selected option index.");
        return Judge(step.Accepted, step.Mistakes, response);
    }

    /// <summary>Multiple-choice lowered step: the picked option's own feedback is returned either way.</summary>
    public static AnswerResult Evaluate(LoweredStep step, int optionIndex)
    {
        if (step.IsTyped) throw new InvalidOperationException($"Step '{step.Id}' is typed; pass the response text.");
        if (optionIndex < 0 || optionIndex >= step.Options.Count)
            throw new ArgumentOutOfRangeException(nameof(optionIndex), "Option index is out of range.");

        var option = step.Options[optionIndex];
        return new AnswerResult(option.Correct, option.Feedback);
    }

    /// <summary>
    /// Benchmark ranking: <paramref name="ranking"/> is candidate ids, best first. Right when it is a permutation of the candidates and each
    /// place holds a candidate with the same recorded value as the recorded order puts there, so candidates that tied (equal bytes) may swap.
    /// </summary>
    public static AnswerResult Evaluate(BenchmarkPredictStep step, IReadOnlyList<string> ranking)
    {
        if (step.Recorded is null) throw new InvalidOperationException($"Step '{step.Id}' has no recorded benchmark; build the pack first.");
        var expected = step.RankedRows();
        var byId = expected.ToDictionary(r => r.CandidateId, StringComparer.Ordinal);

        var places = 0;
        for (var i = 0; i < expected.Count && i < ranking.Count; i++)
            if (byId.TryGetValue(ranking[i], out var given) && step.KeyOf(given) == step.KeyOf(expected[i])) places++;

        var complete = ranking.Count == expected.Count && ranking.Distinct(StringComparer.Ordinal).Count() == expected.Count
            && ranking.All(byId.ContainsKey);
        return complete && places == expected.Count
            ? new AnswerResult(true, null)
            : new AnswerResult(false, $"{places} of {expected.Count} in the right place.");
    }

    /// <summary>
    /// Fill-in-the-blank: each blank is judged on its own, like a typed prediction. A blank missing from
    /// <paramref name="responses"/> counts as left empty, so it is wrong.
    /// </summary>
    public static FillBlankResult Evaluate(FillBlankStep step, IReadOnlyDictionary<string, string> responses) =>
        new(step.Blanks.Select(b =>
        {
            var result = Judge(b.Accepted, b.Mistakes, responses.GetValueOrDefault(b.Id));
            return new BlankResult(b.Id, result.IsCorrect, result.Feedback);
        }).ToList());

    private static AnswerResult Judge(IReadOnlyList<string> accepted, IReadOnlyList<MistakePattern> mistakes, string? response)
    {
        var given = Normalize(response);
        if (given.Length == 0) return new AnswerResult(false, null);
        if (accepted.Any(a => Normalize(a) == given)) return new AnswerResult(true, null);

        foreach (var mistake in mistakes)
            if (Matches(mistake, given))
                return new AnswerResult(false, mistake.Feedback);

        return new AnswerResult(false, null);
    }

    /// <summary>Multiple-choice variant: the picked option's own feedback is returned either way.</summary>
    public static AnswerResult Evaluate(PredictOutputStep step, int optionIndex)
    {
        if (step.IsTyped) throw new InvalidOperationException($"Step '{step.Id}' is typed; pass the response text.");
        if (optionIndex < 0 || optionIndex >= step.Options.Count)
            throw new ArgumentOutOfRangeException(nameof(optionIndex), "Option index is out of range.");

        var option = step.Options[optionIndex];
        return new AnswerResult(option.Correct, option.Feedback);
    }

    private static bool Matches(MistakePattern mistake, string normalisedResponse)
    {
        if (mistake.Answers.Any(a => Normalize(a) == normalisedResponse)) return true;
        if (string.IsNullOrEmpty(mistake.Regex)) return false;
        try
        {
            return Regex.IsMatch(normalisedResponse, mistake.Regex, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false; // a bad authored pattern must never crash a lesson; the validator reports it at pack time
        }
    }
}
