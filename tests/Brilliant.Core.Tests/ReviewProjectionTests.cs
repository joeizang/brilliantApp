using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>Review items and their FSRS schedules, derived from the event log by the Learner State Projector.</summary>
public class ReviewProjectionTests
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

    private static readonly Lesson One = Lesson("one", "q1", "q2");
    private static readonly Lesson Two = Lesson("two", "q1");

    private static ContentGraph Graph(params Lesson[] lessons) => new(
        new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", lessons.Select(l => l.Id).ToList())], lessons);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    private void Finish(Lesson lesson)
    {
        foreach (var s in lesson.Steps) Recorder.CompleteStep(lesson, s.Id);
    }

    private LearnerState Project(DateTimeOffset? now = null, ContentGraph? content = null) =>
        LearnerStateProjector.Project(_log.ReadAll(), content ?? Graph(One, Two), now ?? _clock.Now);

    private void Answer(string itemId, Rating rating, DateTimeOffset at, Lesson? lesson = null)
    {
        lesson ??= One;
        _clock.Now = at;
        Recorder.ReviewAnswered(lesson.Id, lesson.ReviewItems.Single(i => i.Id == itemId), correct: rating != Rating.Again, hintsUsed: 0, TimeSpan.FromSeconds(10), rating);
    }

    // --- Unlocking -------------------------------------------------------------------------------------------------

    [Fact]
    public void Review_items_are_locked_and_not_due_before_their_lesson_is_completed()
    {
        var state = Project();

        Assert.Equal(3, state.Reviews.Count);
        Assert.All(state.Reviews, r => Assert.False(r.IsUnlocked));
        Assert.Empty(state.DueReviews);
    }

    [Fact]
    public void A_half_finished_lesson_does_not_unlock_its_items()
    {
        Recorder.CompleteStep(One, "step.one.intro");
        Recorder.CompleteStep(One, "step.one.q1");

        Assert.Empty(Project().DueReviews);
    }

    [Fact]
    public void Completing_a_lesson_unlocks_exactly_its_own_items_as_new_and_due()
    {
        Finish(One);

        var due = Project().DueReviews;

        Assert.Equal(["review.one.q1", "review.one.q2"], due.Select(r => r.Item.Id));
        Assert.All(due, r => { Assert.True(r.IsUnlocked); Assert.Null(r.Card); });
        Assert.All(due, r => Assert.Equal(One.Id, r.Lesson.Id));
        Assert.Equal("step.one.q1", due[0].Question.Id);
    }

    // --- Scheduling from the log -----------------------------------------------------------------------------------

    [Fact]
    public void Answering_an_item_schedules_it_with_fsrs_and_takes_it_off_the_queue_until_due()
    {
        Finish(One);
        Answer("review.one.q1", Rating.Good, T0.AddHours(1));

        var state = Project(T0.AddHours(2));

        var card = state.Reviews.Single(r => r.Item.Id == "review.one.q1").Card!;
        Assert.Equal(FsrsScheduler.Schedule(null, Rating.Good, T0.AddHours(1)), card);
        Assert.Equal(["review.one.q2"], state.DueReviews.Select(r => r.Item.Id));
        Assert.Contains("review.one.q1", Project(card.Due).DueReviews.Select(r => r.Item.Id));
        Assert.DoesNotContain("review.one.q1", Project(card.Due.AddMinutes(-1)).DueReviews.Select(r => r.Item.Id));
    }

    [Fact]
    public void The_schedule_is_every_answer_folded_through_fsrs_in_time_order()
    {
        Finish(One);
        Answer("review.one.q1", Rating.Good, T0);
        Answer("review.one.q1", Rating.Again, T0.AddDays(3));
        Answer("review.one.q1", Rating.Easy, T0.AddDays(5));

        var expected = FsrsScheduler.Schedule(FsrsScheduler.Schedule(FsrsScheduler.Schedule(null, Rating.Good, T0), Rating.Again, T0.AddDays(3)), Rating.Easy, T0.AddDays(5));

        var item = Project(T0.AddDays(6)).Reviews.Single(r => r.Item.Id == "review.one.q1");
        Assert.Equal(expected, item.Card);
        Assert.Equal(3, item.Answers);
        Assert.Equal(1, item.Card!.Lapses);
    }

    [Fact]
    public void Schedules_do_not_depend_on_the_order_events_arrive_in_or_on_duplicates()
    {
        Finish(One);
        Answer("review.one.q1", Rating.Good, T0);
        Answer("review.one.q1", Rating.Hard, T0.AddDays(3));
        Answer("review.one.q2", Rating.Easy, T0.AddDays(1));
        var inOrder = Project(T0.AddDays(30));

        var shuffled = _log.Items.AsEnumerable().Reverse().Concat(_log.Items).ToList();
        var replayed = LearnerStateProjector.Project(shuffled, Graph(One, Two), T0.AddDays(30));

        Assert.Equal(inOrder.Reviews.Select(r => r.Card), replayed.Reviews.Select(r => r.Card));
        Assert.Equal(inOrder.DueReviews.Select(r => r.Item.Id), replayed.DueReviews.Select(r => r.Item.Id));
    }

    [Fact]
    public void Answers_stamped_at_the_same_instant_are_ordered_by_event_id_so_every_device_agrees()
    {
        Finish(One);
        var a = new ProgressEvent("a", "d1", T0, ProgressEventTypes.ReviewAnswered, One.Id, "step.one.q1", ReviewData("review.one.q1", Rating.Good));
        var b = new ProgressEvent("b", "d2", T0, ProgressEventTypes.ReviewAnswered, One.Id, "step.one.q1", ReviewData("review.one.q1", Rating.Again));

        var ab = LearnerStateProjector.Project(_log.Items.Concat([a, b]), Graph(One, Two), T0.AddDays(1));
        var ba = LearnerStateProjector.Project(_log.Items.Concat([b, a]), Graph(One, Two), T0.AddDays(1));

        Assert.Equal(ab.Reviews.Single(r => r.Item.Id == "review.one.q1").Card, ba.Reviews.Single(r => r.Item.Id == "review.one.q1").Card);
    }

    private static string ReviewData(string item, Rating rating) =>
        JsonSerializer.Serialize(new { item, correct = rating != Rating.Again, hintsUsed = 0, elapsedMs = 1000, rating = (int)rating });

    // --- The due queue ---------------------------------------------------------------------------------------------

    [Fact]
    public void Overdue_items_come_before_new_ones_most_overdue_first()
    {
        Finish(One);
        Finish(Two);
        Answer("review.one.q2", Rating.Good, T0);               // due T0+3d
        Answer("review.one.q1", Rating.Easy, T0.AddDays(-1));   // due T0+15d
        _clock.Now = T0.AddDays(20);

        var order = Project(T0.AddDays(20)).DueReviews.Select(r => r.Item.Id);

        Assert.Equal(["review.one.q2", "review.one.q1", "review.two.q1"], order);
    }

    // --- Content changes -------------------------------------------------------------------------------------------

    [Fact]
    public void Deleting_a_question_step_retires_its_review_item_and_its_history()
    {
        Finish(One);
        Answer("review.one.q1", Rating.Good, T0);
        var without = One with { Steps = One.Steps.Where(s => s.Id != "step.one.q1").ToList(), ReviewItems = One.ReviewItems.Where(i => i.StepId != "step.one.q1").ToList() };

        var state = Project(T0.AddDays(30), Graph(without, Two));

        Assert.DoesNotContain(state.Reviews, r => r.Item.Id == "review.one.q1");
    }

    [Fact]
    public void A_review_item_whose_step_no_longer_exists_is_not_offered()
    {
        Finish(One);
        var dangling = One with { Steps = One.Steps.Where(s => s.Id != "step.one.q1").ToList() };

        Assert.DoesNotContain(Project(content: Graph(dangling, Two)).Reviews, r => r.Item.Id == "review.one.q1");
    }

    [Fact]
    public void Deleting_a_lesson_drops_its_review_items()
    {
        Finish(One);
        Answer("review.one.q1", Rating.Good, T0);

        Assert.All(Project(content: Graph(Two)).Reviews, r => Assert.Equal(Two.Id, r.Lesson.Id));
    }

    [Fact]
    public void Editing_a_question_keeps_its_review_history()
    {
        Finish(One);
        Answer("review.one.q1", Rating.Good, T0);
        var edited = One with { Steps = One.Steps.Select(s => s.Id == "step.one.q1" ? (Step)Q("step.one.q1") with { Title = "reworded" } : s).ToList() };

        var item = Project(T0.AddDays(1), Graph(edited, Two)).Reviews.Single(r => r.Item.Id == "review.one.q1");

        Assert.NotNull(item.Card);
        Assert.Equal("reworded", item.Question.Title);
    }

    [Fact]
    public void A_new_review_item_added_to_a_finished_lesson_is_offered_as_new()
    {
        Finish(One);
        var grown = One with { Steps = [.. One.Steps, Q("step.one.q3")], ReviewItems = [.. One.ReviewItems, new ReviewItem("review.one.q3", "concept.one", "step.one.q3")] };

        var state = Project(content: Graph(grown, Two));

        Assert.Equal(LessonStatus.Completed, state.Lesson(One.Id)!.Status);
        Assert.Contains("review.one.q3", state.DueReviews.Select(r => r.Item.Id));
    }

    [Fact]
    public void Malformed_review_events_are_ignored()
    {
        Finish(One);
        var bad = new[]
        {
            new ProgressEvent("x1", "d", T0, ProgressEventTypes.ReviewAnswered, One.Id, "step.one.q1", null),
            new ProgressEvent("x2", "d", T0, ProgressEventTypes.ReviewAnswered, One.Id, "step.one.q1", "not json"),
            new ProgressEvent("x3", "d", T0, ProgressEventTypes.ReviewAnswered, One.Id, "step.one.q1", """{"item":"review.one.q1","rating":9}"""),
            new ProgressEvent("x4", "d", T0, ProgressEventTypes.ReviewAnswered, One.Id, "step.one.q1", """{"item":"review.nope","rating":3}"""),
        };

        var state = LearnerStateProjector.Project(_log.Items.Concat(bad), Graph(One, Two), T0.AddDays(1));

        Assert.All(state.Reviews.Where(r => r.Lesson.Id == One.Id), r => Assert.Null(r.Card));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"3\"")]
    [InlineData("true")]
    [InlineData("[3]")]
    [InlineData("{}")]
    [InlineData("3.5")]
    [InlineData("99999999999")]
    public void A_rating_that_is_not_a_small_whole_number_is_ignored_rather_than_breaking_projection(string rating)
    {
        Finish(One);
        var bad = new ProgressEvent("x", "d", T0, ProgressEventTypes.ReviewAnswered, One.Id, "step.one.q1", $$"""{"item":"review.one.q1","rating":{{rating}}}""");

        var state = LearnerStateProjector.Project(_log.Items.Concat([bad]), Graph(One, Two), T0.AddDays(1));

        Assert.Null(state.Reviews.Single(r => r.Item.Id == "review.one.q1").Card);
    }

    [Theory]
    [InlineData("""{"item":null,"rating":3}""")]
    [InlineData("""{"item":3,"rating":3}""")]
    [InlineData("""{"item":["review.one.q1"],"rating":3}""")]
    [InlineData("""{"rating":3}""")]
    [InlineData("null")]
    [InlineData("3")]
    [InlineData("[]")]
    [InlineData("\"review.one.q1\"")]
    public void A_payload_or_item_of_the_wrong_kind_is_ignored(string data)
    {
        Finish(One);
        var bad = new ProgressEvent("x", "d", T0, ProgressEventTypes.ReviewAnswered, One.Id, "step.one.q1", data);

        var state = LearnerStateProjector.Project(_log.Items.Concat([bad]), Graph(One, Two), T0.AddDays(1));

        Assert.All(state.Reviews.Where(r => r.Lesson.Id == One.Id), r => Assert.Null(r.Card));
    }

    [Fact]
    public void Review_answers_never_change_lesson_progress()
    {
        Finish(One);
        var before = Project().Lesson(One.Id)!.Progress;
        Answer("review.one.q1", Rating.Again, T0.AddDays(1));

        var after = Project().Lesson(One.Id)!;

        Assert.Equal((before.CompletedCount, LessonStatus.Completed), (after.Progress.CompletedCount, after.Status));
        Assert.True(after.Progress.IsComplete);
    }

    // --- Recording -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_review_answer_is_recorded_with_everything_needed_to_replay_it()
    {
        Finish(One);
        _clock.Now = T0.AddHours(5);

        Recorder.ReviewAnswered(One.Id, One.ReviewItems[0], correct: true, hintsUsed: 1, TimeSpan.FromSeconds(7.5), Rating.Hard);

        var evt = _log.Items.Last();
        Assert.Equal((ProgressEventTypes.ReviewAnswered, One.Id, "step.one.q1", T0.AddHours(5)), (evt.Type, evt.LessonId, evt.StepId, evt.OccurredAt));
        using var data = JsonDocument.Parse(evt.Data!);
        var root = data.RootElement;
        Assert.Equal("review.one.q1", root.GetProperty("item").GetString());
        Assert.True(root.GetProperty("correct").GetBoolean());
        Assert.Equal((1, 7500, 2), (root.GetProperty("hintsUsed").GetInt32(), root.GetProperty("elapsedMs").GetInt32(), root.GetProperty("rating").GetInt32()));
    }

    [Fact]
    public void The_rating_is_inferred_from_correctness_hints_and_time_when_not_given()
    {
        Finish(One);
        var item = Project().DueReviews[0];

        var easy = Recorder.ReviewAnswered(item, correct: true, hintsUsed: 0, TimeSpan.FromSeconds(2));
        var again = Recorder.ReviewAnswered(item, correct: false, hintsUsed: 0, TimeSpan.FromSeconds(2));

        Assert.Equal((Rating.Easy, Rating.Again), (easy, again));
        using var last = JsonDocument.Parse(_log.Items.Last().Data!);
        Assert.Equal(1, last.RootElement.GetProperty("rating").GetInt32());
    }
}
