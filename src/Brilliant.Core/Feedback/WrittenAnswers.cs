using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Feedback;

/// <summary>A design-tradeoff answer the learner wrote, from this or any paired device, as kept in the progress log.</summary>
public sealed record WrittenAnswer(string EventId, string LessonId, string LessonTitle, DesignTradeoffStep Step, int Option, string Justification, string DeviceId, DateTimeOffset At)
{
    public GradeRequest ToGradeRequest() => GradeRequest.For(Step, Option, Justification);
}

/// <summary>
/// Reads the written answers back out of the log, so feedback can be asked for later: a phone with no model records the words, they
/// sync, and a Mac that has one grades them (#167). Content that has since lost the step, and events that don't hold a usable answer,
/// are skipped.
/// </summary>
public static class WrittenAnswers
{
    /// <summary>How many of the newest answers are offered; the list is for catching up, not an archive.</summary>
    public const int MaxListed = 50;

    public static IReadOnlyList<WrittenAnswer> From(IEnumerable<ProgressEvent> events, ContentGraph content) => Scan(events, content).Answers;

    /// <summary>
    /// The answers that can be graded, newest first, and how many others hold words for a step that has since changed. An answer is kept
    /// with a fingerprint of the question it was given for (<see cref="GradeRequest.ContextOf"/>); one whose step no longer matches,
    /// or that was saved before there was a fingerprint, is not offered, because grading it would judge old words against a different question.
    /// </summary>
    public static WrittenAnswerScan Scan(IEnumerable<ProgressEvent> events, ContentGraph content)
    {
        var answers = new List<WrittenAnswer>();
        var changed = 0;
        foreach (var e in events
            .Where(e => e.Type is ProgressEventTypes.StepAnswered or ProgressEventTypes.ReviewAnswered)
            .DistinctBy(e => e.Id)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id, StringComparer.Ordinal))
        {
            switch (Read(e, content))
            {
                case (WrittenAnswer answer, _): if (answers.Count < MaxListed) answers.Add(answer); break;
                case (null, true): changed++; break;
            }
        }
        return new WrittenAnswerScan(answers, changed);
    }

    private static (WrittenAnswer? Answer, bool Changed) Read(ProgressEvent e, ContentGraph content)
    {
        if (e.Data is null || !content.TryGet<Lesson>(e.LessonId, out var lesson)) return (null, false);
        if (lesson.AllSteps().FirstOrDefault(s => s.Id == e.StepId) is not DesignTradeoffStep step) return (null, false);
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            var detail = doc.RootElement;
            // A lesson attempt keeps the detail at the top; a review answer carries it under "answer".
            if (e.Type == ProgressEventTypes.ReviewAnswered && !(detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("answer", out detail))) return (null, false);
            if (detail.ValueKind != JsonValueKind.Object
                || !detail.TryGetProperty("option", out var option) || option.ValueKind != JsonValueKind.Number || !option.TryGetInt32(out var index)
                || !detail.TryGetProperty("justification", out var why) || why.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(why.GetString()))
                return (null, false);
            // From here it is a real answer; whether it still belongs to this step decides if it can be offered.
            if (index < 0 || index >= step.Options.Count
                || !detail.TryGetProperty("context", out var context) || context.ValueKind != JsonValueKind.String
                || context.GetString() != GradeRequest.ContextOf(step, index))
                return (null, true);
            return (new WrittenAnswer(e.Id, e.LessonId, lesson.Title, step, index, why.GetString()!, e.DeviceId, e.OccurredAt), false);
        }
        catch (JsonException) { return (null, false); }
    }
}

/// <summary>What <see cref="WrittenAnswers.Scan"/> found: the answers that can be graded, and how many more are for a step that has changed.</summary>
public sealed record WrittenAnswerScan(IReadOnlyList<WrittenAnswer> Answers, int Changed);
