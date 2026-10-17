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

    public static IReadOnlyList<WrittenAnswer> From(IEnumerable<ProgressEvent> events, ContentGraph content) => events
        .Where(e => e.Type is ProgressEventTypes.StepAnswered or ProgressEventTypes.ReviewAnswered)
        .DistinctBy(e => e.Id)
        .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id, StringComparer.Ordinal)
        .Select(e => Read(e, content))
        .OfType<WrittenAnswer>()
        .Take(MaxListed)
        .ToList();

    private static WrittenAnswer? Read(ProgressEvent e, ContentGraph content)
    {
        if (e.Data is null || !content.TryGet<Lesson>(e.LessonId, out var lesson)) return null;
        if (lesson.AllSteps().FirstOrDefault(s => s.Id == e.StepId) is not DesignTradeoffStep step) return null;
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            var detail = doc.RootElement;
            // A lesson attempt keeps the detail at the top; a review answer carries it under "answer".
            if (e.Type == ProgressEventTypes.ReviewAnswered && !(detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("answer", out detail))) return null;
            if (detail.ValueKind != JsonValueKind.Object
                || !detail.TryGetProperty("option", out var option) || option.ValueKind != JsonValueKind.Number || !option.TryGetInt32(out var index)
                || index < 0 || index >= step.Options.Count
                || !detail.TryGetProperty("justification", out var why) || why.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(why.GetString()))
                return null;
            return new WrittenAnswer(e.Id, e.LessonId, lesson.Title, step, index, why.GetString()!, e.DeviceId, e.OccurredAt);
        }
        catch (JsonException) { return null; }
    }
}
