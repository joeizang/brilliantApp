namespace Brilliant.Core.Progress;

public static class ProgressEventTypes
{
    public const string StepAnswered = "StepAnswered";
    public const string StepCompleted = "StepCompleted";

    /// <summary>A write-code submission, recorded every time the learner runs their code against the tests.</summary>
    public const string CodeSubmitted = "CodeSubmitted";

    /// <summary>
    /// The learner revealed a rung of a step's hint ladder. Data holds the 1-based <c>level</c> reached and, when it happened in
    /// Review, the review <c>item</c> ID. Recorded once per rung, so the events of one question are its hint count.
    /// </summary>
    public const string HintUsed = "HintUsed";

    /// <summary>Recorded once, when the last step of a lesson is completed. Lesson-level events carry an empty StepId.</summary>
    public const string LessonCompleted = "LessonCompleted";

    /// <summary>
    /// A review item was answered. Data holds the item ID, correctness, hints used, time taken and the FSRS rating
    /// that was inferred; the rating is stored so the schedule replays identically even if inference is tuned later.
    /// </summary>
    public const string ReviewAnswered = "ReviewAnswered";

    /// <summary>The learner chose their daily goal. Data holds <c>steps</c>; the latest setting wins. Lesson-level, empty IDs.</summary>
    public const string DailyGoalSet = "DailyGoalSet";

    /// <summary>
    /// The learner began a daily session. <c>LessonId</c> is the lesson the session continues with (empty when nothing was left to
    /// learn). Whether the session is still running, and which phase it is in, is derived, never recorded.
    /// </summary>
    public const string SessionStarted = "SessionStarted";

    /// <summary>The learner reached the end-of-session summary.</summary>
    public const string SessionCompleted = "SessionCompleted";
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
