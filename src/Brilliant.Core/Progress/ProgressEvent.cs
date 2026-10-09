namespace Brilliant.Core.Progress;

public static class ProgressEventTypes
{
    public const string StepAnswered = "StepAnswered";
    public const string StepCompleted = "StepCompleted";
}

/// <summary>
/// An immutable fact about the learner's progress. Progress is always derived from these, never stored as mutable state.
/// <see cref="Data"/> is type-specific JSON (e.g. the selected options of a StepAnswered).
/// </summary>
public sealed record ProgressEvent(
    string Id,
    string DeviceId,
    DateTimeOffset OccurredAt,
    string Type,
    string LessonId,
    string StepId,
    string? Data = null);

/// <summary>A page of events plus the cursor to pass to the next <see cref="IProgressEventLog.ReadSince"/> call.</summary>
public sealed record EventPage(IReadOnlyList<ProgressEvent> Events, long Cursor);

/// <summary>Append-only event log. There is deliberately no update or delete.</summary>
public interface IProgressEventLog
{
    /// <summary>Appends the event. Idempotent by event ID: returns false (and changes nothing) if the ID already exists.</summary>
    bool Append(ProgressEvent evt);

    /// <summary>All events in append order.</summary>
    IReadOnlyList<ProgressEvent> ReadAll();

    /// <summary>Events appended after <paramref name="cursor"/> (0 = from the start), with the new cursor.</summary>
    EventPage ReadSince(long cursor);
}
