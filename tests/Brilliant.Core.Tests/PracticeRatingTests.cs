using Brilliant.Core.Content;
using Brilliant.Core.Practice;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>
/// How practice and interview answers move the schedule (#129): interview ratings, parts an interview ended without, and practice on an
/// item already answered the same day. Each case is checked through the projector, replaying the log.
/// </summary>
public class PracticeRatingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static readonly WriteCodeStep P1 = new("step.a.p1", "p1", "?", "python", "", "f", [new CodeTest("1", "1")]) { Pattern = "pattern.a" };

    private static readonly Lesson A = new("lesson.a", "a", "track.t", [P1]) { Patterns = [new Pattern("pattern.a", "Pattern a")] };

    private static readonly ContentGraph Content = new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [A.Id])], [A]);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    public PracticeRatingTests() => Recorder.CompleteStep(A, P1.Id);

    private LearnerState Project() => LearnerStateProjector.Project(_log.ReadAll(), Content, _clock.Now);

    private ReviewItemState? Item(string id) => Project().Reviews.SingleOrDefault(r => r.Item.Id == id);

    private Rating Pick(bool correct, int seconds, PracticeMode mode = PracticeMode.Practice, string run = "run.1") =>
        Recorder.PracticePicked(A.Id, P1.Id, correct ? "pattern.a" : "pattern.b", correct, TimeSpan.FromSeconds(seconds), mode, run);

    private Rating Solve(bool correct, int seconds, PracticeMode mode = PracticeMode.Practice, string run = "run.1") =>
        Recorder.PracticeSolved(A.Id, P1, P1.Id, correct, 0, TimeSpan.FromSeconds(seconds), mode, run);

    // --- Interview ratings ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, 5, Rating.Again)]
    [InlineData(true, 15, Rating.Easy)]
    [InlineData(true, 16, Rating.Good)]
    [InlineData(true, 3600, Rating.Good)]   // never Hard for time: the clock sets the pace
    public void An_interview_answer_is_rated_without_a_slow_threshold(bool correct, int seconds, Rating expected) =>
        Assert.Equal(expected, RatingInference.InferInterview(correct, TimeSpan.FromSeconds(seconds), RatingInference.PickThresholds.Fast));

    [Fact]
    public void A_slow_right_solve_is_Hard_in_practice_but_Good_in_an_interview()
    {
        Assert.Equal(Rating.Hard, Solve(true, 600, PracticeMode.Practice, "run.1"));
        _clock.Now = T0.AddDays(1);
        Assert.Equal(Rating.Good, Solve(true, 600, PracticeMode.Interview, "run.2"));
        Assert.Equal(Rating.Easy, Solve(true, 60, PracticeMode.Interview, "run.3"));          // write-code: fast to 90 s
        Assert.Equal(Rating.Again, Solve(false, 60, PracticeMode.Interview, "run.4"));
    }

    [Fact]
    public void A_slow_right_pick_is_Hard_in_practice_but_Good_in_an_interview()
    {
        Assert.Equal(Rating.Hard, Pick(true, 90, PracticeMode.Practice, "run.1"));
        Assert.Equal(Rating.Good, Pick(true, 90, PracticeMode.Interview, "run.2"));
        Assert.Equal(Rating.Easy, Pick(true, 10, PracticeMode.Interview, "run.3"));
    }

    [Fact]
    public void The_interview_rating_is_what_schedules_the_item()
    {
        Solve(true, 600, PracticeMode.Interview);
        var resolve = Item("resolve." + P1.Id)!;
        Assert.Equal(Rating.Good, resolve.LastRating);
        Assert.Equal(FsrsScheduler.Schedule(null, Rating.Good, T0), resolve.Card);
    }

    // --- Unanswered interview parts ---------------------------------------------------------------------------------

    [Fact]
    public void An_unanswered_part_leaves_an_existing_schedule_alone()
    {
        Pick(true, 10);
        var before = Item("pick." + P1.Id)!;

        _clock.Now = T0.AddDays(5);
        Recorder.PracticePicked(A.Id, P1.Id, null, false, TimeSpan.Zero, PracticeMode.Interview, "run.2");

        var after = Item("pick." + P1.Id)!;
        Assert.Equal((before.Answers, before.Card, before.LastRating), (after.Answers, after.Card, after.LastRating));
    }

    [Theory]
    [InlineData("""{"step":"step.a.p1","part":"pick","correct":false,"picked":null,"rating":1,"mode":"interview","run":"r"}""", false)]
    [InlineData("""{"step":"step.a.p1","part":"pick","correct":false,"picked":"pattern.b","rating":1,"mode":"interview","run":"r"}""", true)]
    [InlineData("""{"step":"step.a.p1","part":"solve","correct":false,"rating":1,"mode":"interview","run":"r"}""", true)]
    [InlineData("""{"step":"step.a.p1","part":"solve","correct":false,"rating":1,"mode":"interview","run":"r","answered":false}""", false)]
    [InlineData("""{"step":"step.a.p1","part":"solve","correct":true,"rating":3,"answered":"no"}""", true)]
    public void Answered_is_read_from_the_event_or_inferred_for_older_events(string data, bool answered)
    {
        var e = new ProgressEvent("e1", "d", T0, ProgressEventTypes.PracticeAnswered, A.Id, P1.Id, data);
        Assert.Equal(answered, PracticeAnswer.From(e)!.Answered);
    }

    [Fact]
    public void Answered_round_trips()
    {
        Recorder.PracticePicked(A.Id, P1.Id, null, false, TimeSpan.Zero, PracticeMode.Interview, "run.1");
        Pick(true, 10, run: "run.2");
        var answers = _log.Items.Select(PracticeAnswer.From).OfType<PracticeAnswer>().ToList();
        Assert.Equal([false, true], answers.Select(a => a.Answered));
    }

    // --- Practice on the same day ------------------------------------------------------------------------------------

    [Fact]
    public void Practising_one_problem_again_the_same_day_moves_nothing()
    {
        Pick(true, 10, run: "run.1");
        _clock.Now = T0.AddHours(3);
        Pick(true, 10, run: "run.2");
        Pick(true, 10, PracticeMode.Interview, "run.3");

        var pick = Item("pick." + P1.Id)!;
        Assert.Equal(1, pick.Answers);
        Assert.Equal(FsrsScheduler.Schedule(null, Rating.Easy, T0), pick.Card);

        _clock.Now = T0.AddDays(1);
        Pick(false, 10, run: "run.4");
        Assert.Equal((2, Rating.Again), (Item("pick." + P1.Id)!.Answers, Item("pick." + P1.Id)!.LastRating));
    }

    [Fact]
    public void Practice_after_a_review_answer_the_same_day_moves_nothing_but_a_review_after_practice_does()
    {
        Pick(true, 10, run: "run.1");
        _clock.Now = T0.AddDays(4);
        Recorder.ReviewAnswered(Item("pick." + P1.Id)!, correct: true, hintsUsed: 0, TimeSpan.FromSeconds(10));
        _clock.Now = T0.AddDays(4).AddHours(1);
        Pick(false, 10, run: "run.2");
        Assert.Equal((2, Rating.Easy), (Item("pick." + P1.Id)!.Answers, Item("pick." + P1.Id)!.LastRating));

        // Review is due-driven: it always counts, even after practice that day.
        _clock.Now = T0.AddDays(20);
        Pick(true, 10, run: "run.3");
        _clock.Now = T0.AddDays(20).AddHours(1);
        Recorder.ReviewAnswered(Item("pick." + P1.Id)!, correct: false, hintsUsed: 0, TimeSpan.FromSeconds(10));
        Assert.Equal((4, Rating.Again), (Item("pick." + P1.Id)!.Answers, Item("pick." + P1.Id)!.LastRating));
    }

    [Fact]
    public void The_same_day_is_the_learners_local_day()
    {
        var lagos = TimeSpan.FromHours(1);
        _clock.Now = new DateTimeOffset(2026, 10, 10, 23, 30, 0, lagos);
        Pick(true, 10, run: "run.1");
        _clock.Now = new DateTimeOffset(2026, 10, 11, 0, 30, 0, lagos);   // the next local day, though under a day later
        Pick(true, 10, run: "run.2");

        Assert.Equal(2, Item("pick." + P1.Id)!.Answers);
    }

    // --- Why practice counts as fully as Review ---------------------------------------------------------------------

    [Fact]
    public void A_right_answer_long_before_the_item_is_due_grows_it_less_than_one_at_its_due_date()
    {
        var first = FsrsScheduler.Schedule(null, Rating.Good, T0);
        var early = FsrsScheduler.Schedule(first, Rating.Good, T0.AddDays(1));
        var due = FsrsScheduler.Schedule(first, Rating.Good, first.Due);
        Assert.True(early.Stability < due.Stability);
    }
}
