using Brilliant.Core.Content;
using Brilliant.Core.Practice;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Tests;

/// <summary>The practice streak and Today's practice suggestion (#128), replayed through the projector.</summary>
public class PracticeStreakTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    /// <summary>A lesson teaching pattern.{name} with <paramref name="problems"/> problems tagged with it (none: it teaches but offers nothing).</summary>
    private static Lesson Teaching(string name, int problems = 1) =>
        new($"lesson.{name}", name, "track.t", [
            new ChoiceStep($"step.{name}.q", "Q", "?", false, [new ChoiceOption("a", true, null), new ChoiceOption("b", false, null)]),
            .. Enumerable.Range(1, problems).Select(i =>
                (Step)new WriteCodeStep($"step.{name}.p{i}", "p", "?", "python", "", "f", [new CodeTest("1", "1")]) { Pattern = $"pattern.{name}" }),
        ])
        {
            Patterns = [new Pattern($"pattern.{name}", $"Pattern {name}")],
        };

    private static readonly Lesson A = Teaching("a"), B = Teaching("b"), C = Teaching("c"), D = Teaching("d", problems: 0);

    private static readonly ContentGraph Content =
        new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", new[] { A, B, C, D }.Select(l => l.Id).ToList())], [A, B, C, D]);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    private void Finish(params Lesson[] lessons)
    {
        foreach (var lesson in lessons)
        foreach (var s in lesson.Steps)
            Recorder.CompleteStep(lesson, s.Id);
    }

    private LearnerState Project(DateTimeOffset? now = null) => LearnerStateProjector.Project(_log.ReadAll(), Content, now ?? _clock.Now);

    private void PractiseOn(int day, bool answered = true)
    {
        _clock.Now = T0.AddDays(day);
        Recorder.PracticePicked(A.Id, "step.a.p1", answered ? "pattern.a" : null, answered, TimeSpan.FromSeconds(10),
            answered ? PracticeMode.Practice : PracticeMode.Interview, $"run.{day}");
    }

    // --- The streak --------------------------------------------------------------------------------------------------

    [Fact]
    public void Without_practice_there_is_no_streak()
    {
        Finish(A, B, C);
        Assert.Equal((0, false), (Project().Today.PracticeStreak, Project().Today.PractisedToday));
    }

    [Fact]
    public void Consecutive_days_of_practice_make_the_streak_which_holds_through_a_day_not_yet_practised()
    {
        Finish(A, B, C);
        PractiseOn(0);
        PractiseOn(1);
        PractiseOn(2);
        PractiseOn(2);   // a second answer the same day is the same day

        Assert.Equal((3, true), (Project().Today.PracticeStreak, Project().Today.PractisedToday));

        var tomorrow = Project(T0.AddDays(3)).Today;
        Assert.Equal((3, false), (tomorrow.PracticeStreak, tomorrow.PractisedToday));

        Assert.Equal(0, Project(T0.AddDays(4)).Today.PracticeStreak);   // a whole day missed
    }

    [Fact]
    public void A_missed_day_starts_the_streak_again()
    {
        Finish(A, B, C);
        PractiseOn(0);
        PractiseOn(2);

        Assert.Equal(1, Project().Today.PracticeStreak);
    }

    [Fact]
    public void An_interview_answer_counts_but_a_part_it_ended_without_does_not()
    {
        Finish(A, B, C);
        _clock.Now = T0;
        Recorder.PracticePicked(A.Id, "step.a.p1", "pattern.a", true, TimeSpan.FromSeconds(10), PracticeMode.Interview, "run.i");
        Assert.Equal(1, Project().Today.PracticeStreak);

        PractiseOn(1, answered: false);
        Assert.Equal((1, false), (Project().Today.PracticeStreak, Project().Today.PractisedToday));
    }

    [Fact]
    public void The_day_is_the_one_the_answer_recorded()
    {
        Finish(A, B, C);
        _clock.Zone = TimeZoneInfo.CreateCustomTimeZone("Lagos", TimeSpan.FromHours(1), "Lagos", "Lagos");
        _clock.Now = new DateTimeOffset(2026, 10, 10, 23, 30, 0, TimeSpan.Zero);   // 00:30 on the 11th in Lagos
        Recorder.PracticePicked(A.Id, "step.a.p1", "pattern.a", true, TimeSpan.FromSeconds(10), PracticeMode.Practice, "run.1");

        Assert.True(Project(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(1))).Today.PractisedToday);
    }

    [Fact]
    public void Practice_is_not_a_step_toward_the_goal_or_its_streak()
    {
        Finish(A, B, C);
        var before = Project().Today;
        PractiseOn(0);
        var after = Project().Today;

        Assert.Equal((before.DoneToday, before.Streak), (after.DoneToday, after.Streak));
    }

    // --- Today's suggestion ------------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_is_suggested_until_Practice_is_open()
    {
        Finish(A, B);
        Assert.Empty(PracticeSelector.Suggestion(Project()));
    }

    [Fact]
    public void With_no_reviews_due_the_weakest_learned_patterns_with_problems_are_suggested()
    {
        Finish(A, B, C, D);
        // A practised right, so it is stronger than B and C; D has no problems to offer.
        PractiseOn(0);
        Recorder.PracticeSolved(A.Id, A.Steps[1], "step.a.p1", true, 0, TimeSpan.FromSeconds(30), PracticeMode.Practice, "run.0");

        var state = Project();
        Assert.Equal(0, state.Today.ReviewsDue);
        Assert.Equal(["pattern.b", "pattern.c"], PracticeSelector.Suggestion(state).Select(p => p.Pattern.Id));
    }

    [Fact]
    public void Nothing_is_suggested_while_reviews_are_due()
    {
        Finish(A, B, C);
        PractiseOn(0);

        var later = Project(T0.AddDays(30));
        Assert.True(later.Today.ReviewsDue > 0);
        Assert.Empty(PracticeSelector.Suggestion(later));
    }
}
