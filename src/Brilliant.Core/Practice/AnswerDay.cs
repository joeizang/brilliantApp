using System.Globalization;
using System.Text.Json;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Practice;

/// <summary>
/// The local day an answer (ReviewAnswered or PracticeAnswered) was given, as recorded in its <c>day</c> field. It decides whether a
/// practice answer comes on the same day as the item's previous answer, so it must not depend on where or when the log is replayed:
/// the recorder stores the learner's local date at the moment of answering, and an older event without one falls back to its UTC date.
/// </summary>
public static class AnswerDay
{
    internal const string Format = "yyyy-MM-dd";

    /// <summary>The learner's local date at <paramref name="clock"/>'s now, in its local time zone.</summary>
    public static DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), clock.LocalTimeZone).DateTime);

    /// <summary>The recorded day of an answer event, or its UTC date when it has none (or a malformed one).</summary>
    public static DateOnly Of(ProgressEvent e)
    {
        if (e.Data is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("day", out var d)
                    && d.ValueKind == JsonValueKind.String && Parse(d.GetString()) is { } day)
                    return day;
            }
            catch (JsonException) { }
        }
        return DateOnly.FromDateTime(e.OccurredAt.UtcDateTime);
    }

    internal static DateOnly? Parse(string? text) =>
        DateOnly.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
}
