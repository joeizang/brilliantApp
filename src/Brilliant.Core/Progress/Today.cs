using Brilliant.Core.Content;

namespace Brilliant.Core.Progress;

/// <summary>A daily session that has been started and not yet finished.</summary>
/// <param name="LessonId">The lesson the session continues with after Review; null when there was none to continue.</param>
public sealed record SessionState(string? LessonId, DateTimeOffset StartedAt);

/// <summary>What the Today screen shows, derived from the log.</summary>
/// <param name="GoalSteps">The daily goal in steps; a step is a lesson step completed or a review answered.</param>
/// <param name="DoneToday">Steps counted toward today's goal so far.</param>
/// <param name="Streak">Consecutive days that met their goal, ending today if today is met, else yesterday (a day still in progress doesn't break it).</param>
/// <param name="NextLesson">The lesson to continue: the first one under way, else the first one not yet started; null when everything is finished.</param>
/// <param name="ReviewsDue">How many reviews today's queue offers.</param>
/// <param name="Session">Today's unfinished session, if any.</param>
public sealed record TodayState(int GoalSteps, int DoneToday, int Streak, LessonState? NextLesson, int ReviewsDue, SessionState? Session)
{
    public const int DefaultGoal = 10;
    public const int MinGoal = 1;
    public const int MaxGoal = 500;

    public bool GoalMet => DoneToday >= GoalSteps;

    /// <summary>Whether there is anything for a session to do.</summary>
    public bool HasWork => ReviewsDue > 0 || NextLesson is not null;

    public static int ClampGoal(int steps) => Math.Clamp(steps, MinGoal, MaxGoal);
}

/// <summary>One concept whose mastery moved during a session.</summary>
public sealed record MasteryChange(Concept Concept, LessonState Lesson, double Before, double After)
{
    public double Delta => After - Before;
    public bool IsGain => Delta > 0;
}

/// <summary>The end-of-session summary: how much was done, how well, and what it did to the learner's mastery.</summary>
/// <param name="Attempts">Answers given (steps, code submissions and reviews); <paramref name="Correct"/> of them were right.</param>
/// <param name="MasteryChanges">Concepts practised whose mastery changed, the biggest movers first.</param>
public sealed record SessionSummary(int Steps, int Reviews, int Attempts, int Correct, IReadOnlyList<MasteryChange> MasteryChanges)
{
    /// <summary>The share of answers that were right (0–1); null when nothing was answered.</summary>
    public double? Accuracy => Attempts == 0 ? null : (double)Correct / Attempts;
}
