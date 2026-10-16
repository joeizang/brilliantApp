using System.Globalization;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>
/// Everything a learner can see, flattened to one string so two event logs can be compared by what they mean rather than how they are
/// stored. Compiled into the server tests too (the two-device convergence test), so there is one definition of "the same learner state".
/// </summary>
public static class LearnerStateFingerprint
{
    public static string Of(IEnumerable<ProgressEvent> events, ContentGraph content, DateTimeOffset now)
    {
        var all = events.ToList();
        var state = LearnerStateProjector.Project(all, content, now);
        var lines = new List<string>();

        foreach (var l in state.Tracks.SelectMany(t => t.Lessons))
            lines.Add($"lesson {l.Lesson.Id} {l.Status} {l.Progress.CompletedCount}/{l.Progress.TotalSteps} next={l.Progress.NextStep?.Id} new={l.Progress.NewSteps.Count} recorded={l.Progress.CompletionRecorded} testedOut={l.Progress.TestedOut}");

        var t = state.Today;
        lines.Add($"today goal={t.GoalSteps} done={t.DoneToday} streak={t.Streak} due={t.ReviewsDue} next={t.NextLesson?.Lesson.Id} session={t.Session?.LessonId}@{t.Session?.StartedAt.UtcTicks}");

        foreach (var r in state.Reviews)
            lines.Add($"review {r.Item.Id} unlocked={r.IsUnlocked} answers={r.Answers} last={r.LastRating} hints={r.AttemptHints}/{r.LessonHints} card={Card(r.Card)}");

        lines.Add($"queue [{string.Join(",", state.Queue.Items.Select(i => i.Item.Id))}] waiting={state.Queue.Waiting} done={state.Queue.DoneToday}");

        foreach (var c in state.Concepts)
            lines.Add($"concept {c.Concept.Id} {c.Mastery.ToString("R", CultureInfo.InvariantCulture)} {c.Level} attempts={c.History.Count} correct={c.History.Count(h => h.Correct)}");

        foreach (var p in state.Patterns)
            lines.Add($"pattern {p.Pattern.Id} {p.Mastery.ToString("R", CultureInfo.InvariantCulture)} {p.Level} practised={p.StepsPractised}/{p.StepsTagged}");

        foreach (var f in FlaggedSteps.From(all, content))
            lines.Add($"flag {f.LessonId}/{f.StepId} {f.Note} {f.FlaggedAt.UtcTicks}");

        return string.Join('\n', lines);
    }

    private static string Card(CardState? c) => c is null ? "new" : string.Create(CultureInfo.InvariantCulture,
        $"s={c.Stability:R} d={c.Difficulty:R} last={c.LastReview.UtcTicks} due={c.Due.UtcTicks} reps={c.Reps} lapses={c.Lapses}");
}

/// <summary>A small course and a two-device history over it, with the awkward cases: the same step done on both devices, the same review answered on both, a tie on the clock, and settings changed on each.</summary>
public static class SyncFixtures
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private static ChoiceStep Q(string id) => new(id, id, "?", false, [new ChoiceOption("a", true, null), new ChoiceOption("b", false, null)]);

    private static Lesson Lesson(string name, params string[] questions)
    {
        var steps = new List<Step> { new ExplainStep($"step.{name}.intro", "intro", "b", [], null) };
        steps.AddRange(questions.Select(q => Q($"step.{name}.{q}")));
        return new Lesson($"lesson.{name}", name, "track.t", steps)
        {
            Concepts = [new Concept($"concept.{name}", name)],
            ReviewItems = questions.Select(q => new ReviewItem($"review.{name}.{q}", $"concept.{name}", $"step.{name}.{q}")).ToList(),
        };
    }

    public static readonly Lesson One = Lesson("one", "q1", "q2");
    public static readonly Lesson Two = Lesson("two", "q1");
    public static readonly Lesson Three = Lesson("three", "q1");

    public static readonly ContentGraph Content = new(
        new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [One.Id, Two.Id, Three.Id])], [One, Two, Three]);

    public sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// What two devices did while apart, in the order it happened. <paramref name="a"/> and <paramref name="b"/> record onto their own
    /// logs; <paramref name="advance"/> moves the shared wall clock. Both devices' clocks agree (a stricter test than skew).
    /// </summary>
    public static void TwoDevicesApart(ProgressRecorder a, ProgressRecorder b, Clock clock)
    {
        void Tick(int minutes) => clock.Now = clock.Now.AddMinutes(minutes);

        // A starts lesson one and sets a goal.
        a.StartSession(One.Id);
        a.SetDailyGoal(5);
        Tick(1);
        a.CompleteStep(One, "step.one.intro");
        Tick(1);
        a.StepAnswered(One.Id, "step.one.q1", correct: false, selected: [1]);
        a.HintUsed(One.Id, "step.one.q1", 1);
        Tick(1);
        a.StepAnswered(One.Id, "step.one.q1", correct: true, selected: [0]);
        a.CompleteStep(One, "step.one.q1");

        // B, meanwhile, does the same first step (a duplicate in meaning, a different event) and carries on further.
        b.CompleteStep(One, "step.one.intro");
        Tick(1);
        b.StepAnswered(One.Id, "step.one.q1", correct: true, selected: [0]);
        b.CompleteStep(One, "step.one.q1");
        b.CompleteStep(One, "step.one.q2");
        Tick(2);
        b.StepFlagged(One.Id, "step.one.q2", "the second option is also right");
        b.SetDailyGoal(8);
        b.CompleteStep(Two, "step.two.intro");

        // Both answer the same review item, a day later, one after the other and at the very same instant.
        Tick(24 * 60);
        a.ReviewAnswered(One.Id, One.ReviewItems[0], correct: true, hintsUsed: 0, TimeSpan.FromSeconds(8), Rating.Good);
        b.ReviewAnswered(One.Id, One.ReviewItems[0], correct: false, hintsUsed: 1, TimeSpan.FromSeconds(30), Rating.Again);   // same instant: a tie
        Tick(1);
        a.CompleteStep(One, "step.one.q2");
        a.ReviewAnswered(One.Id, One.ReviewItems[1], correct: true, hintsUsed: 0, TimeSpan.FromSeconds(12), Rating.Easy);
        Tick(3);
        b.SetDailyGoal(12);
        b.CompleteStep(Two, "step.two.q1");
        b.CompleteSession();
        a.StepFlagged(One.Id, "step.one.q1");
    }
}
