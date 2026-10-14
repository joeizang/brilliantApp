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

    /// <summary>
    /// One part of a practice or interview problem was answered: the pattern pick (<c>part: "pick"</c>, with the <c>picked</c> pattern ID,
    /// null when the learner never chose) or the solution (<c>part: "solve"</c>). Data also holds correctness, hints, time, the inferred
    /// rating, the <c>mode</c> ("practice" or "interview") and the <c>run</c>: the practice visit or interview run it belongs to. It
    /// schedules the step's derived <c>pick.</c> or <c>resolve.</c> review item like a ReviewAnswered, and never touches lesson progress.
    /// </summary>
    public const string PracticeAnswered = "PracticeAnswered";

    /// <summary>
    /// A timed interview run began. Data holds the <c>run</c> ID, the <c>preset</c> ("phone-screen", "interview" or "custom"), the problems as
    /// <c>steps</c> (each <c>{ step, lesson, choices }</c>, the patterns offered in order) and <c>limitSeconds</c>. The problem set and the
    /// start time (the event's own time) are fixed here, so the clock runs on whether the app is open or not. Lesson-level, empty IDs.
    /// </summary>
    public const string InterviewStarted = "InterviewStarted";

    /// <summary>
    /// A timed interview run ended: time ran out, the learner finished early, or every problem was submitted. Data holds the <c>run</c>.
    /// Written after the unanswered parts are recorded as wrong. Lesson-level, empty IDs.
    /// </summary>
    public const string InterviewFinished = "InterviewFinished";

    /// <summary>
    /// Time spent on one problem of an open interview run, answered or not (#128): data holds the <c>run</c>, the <c>step</c> and
    /// <c>spentMs</c>, the problem's total time on screen so far. Written as the learner switches problems, answers, leaves, finishes and
    /// every so often while it is on screen; the largest total counts. Lesson-level, empty IDs.
    /// </summary>
    public const string InterviewTimeSpent = "InterviewTimeSpent";

    /// <summary>The learner chose their daily goal. Data holds <c>steps</c>; the latest setting wins. Lesson-level, empty IDs.</summary>
    public const string DailyGoalSet = "DailyGoalSet";

    /// <summary>
    /// The learner began a daily session. <c>LessonId</c> is the lesson the session continues with (empty when nothing was left to
    /// learn). Whether the session is still running, and which phase it is in, is derived, never recorded.
    /// </summary>
    public const string SessionStarted = "SessionStarted";

    /// <summary>The learner reached the end-of-session summary.</summary>
    public const string SessionCompleted = "SessionCompleted";

    /// <summary>
    /// The learner marked a step as confusing or wrong. Data holds the optional <c>note</c>. It has no effect on progress;
    /// <see cref="FlaggedSteps"/> reads these back for the author.
    /// </summary>
    public const string StepFlagged = "StepFlagged";
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
