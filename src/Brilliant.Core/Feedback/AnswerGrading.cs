using Brilliant.Core.Content;

namespace Brilliant.Core.Feedback;

/// <summary>
/// Everything a grader sees of a written answer: the scenario, the option the learner picked, the authored rubric, model answer and red
/// flags, and the learner's own words. Built from the step, so a grader never needs the content model.
/// </summary>
public sealed record GradeRequest(
    string Scenario, string Option, string Answer, string ModelAnswer, IReadOnlyList<string> RubricPoints, IReadOnlyList<string> RedFlags)
{
    public static GradeRequest For(DesignTradeoffStep step, int option, string answer) =>
        new(step.Prompt, step.Options[option].Text, answer, step.ModelAnswer, [.. step.Rubric.Select(p => p.Text)], [.. step.RedFlags]);

    /// <summary>
    /// A short fingerprint of everything the learner was answering against: the scenario, the option's text, the model answer, the
    /// rubric and the red flags, but not their words. Saved with a written answer, it tells a later reader whether the step still
    /// means what it meant then (a content update can reorder options or reword the rubric under the same step ID).
    /// </summary>
    public static string ContextOf(DesignTradeoffStep step, int option) => For(step, option, "").Fingerprint();

    public string Fingerprint()
    {
        var canonical = System.Text.Json.JsonSerializer.Serialize(new object[] { Scenario, Option, ModelAnswer, RubricPoints, RedFlags });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))[..16].ToLowerInvariant();
    }
}

/// <summary>Whether the answer made rubric point <see cref="Index"/> (zero-based), and a short note on why.</summary>
public sealed record PointFeedback(int Index, string Point, bool Hit, string Note);

/// <summary>The answer claimed red flag <see cref="Index"/> (zero-based).</summary>
public sealed record FlagFinding(int Index, string Flag, string Note);

/// <summary>A grader's advice on one answer: a verdict per rubric point, in rubric order, and the red flags it found. Advisory only.</summary>
public sealed record AnswerFeedback(IReadOnlyList<PointFeedback> Points, IReadOnlyList<FlagFinding> RedFlagsFound);

public enum AnswerGraderFailure
{
    /// <summary>Nothing answered: the model isn't running, the connection failed or it took too long, or it answered with an error.</summary>
    Unavailable,

    /// <summary>The model server is up but doesn't have the model the app asks for.</summary>
    ModelMissing,

    /// <summary>The model answered, but not in the shape the schema asked for.</summary>
    BadResponse,
}

/// <summary>Feedback couldn't be had. <see cref="Exception.Message"/> says why in words the learner can act on.</summary>
public sealed class AnswerGraderException(AnswerGraderFailure failure, string message) : Exception(message)
{
    public AnswerGraderFailure Failure => failure;
}

/// <summary>Gives advisory feedback on a written answer. It never sets a rating: the learner's own self-assessment does.</summary>
public interface IAnswerGrader
{
    /// <summary>
    /// Whether the learner currently gets feedback from this grader. A grader that is off (the phone's, until the learner opts in to the
    /// server) is treated as absent: no feedback section, no request.
    /// </summary>
    bool Enabled => true;

    /// <exception cref="AnswerGraderException">No usable feedback could be had; the step is finished regardless.</exception>
    Task<AnswerFeedback> GradeAsync(GradeRequest request, CancellationToken ct = default);
}
