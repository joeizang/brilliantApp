using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>HintUsed events, and what using hints does to a review item's rating and schedule.</summary>
public class HintTests
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

    private static readonly Lesson Lesson = new("lesson.l", "Lesson", "track.t",
        [
            new ExplainStep("step.intro", "Intro", "b", [], null),
            new ChoiceStep("step.q", "Q", "?", false, [new ChoiceOption("a", true, null), new ChoiceOption("b", false, null)])
            {
                Hints = ["nudge", "pattern", "partial", "solution"],
            },
        ])
    {
        Concepts = [new Concept("concept.c", "C")],
        ReviewItems = [new ReviewItem("review.q", "concept.c", "step.q")],
    };

    private static readonly ContentGraph Content = new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [Lesson.Id])], [Lesson]);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    private void FinishLesson()
    {
        foreach (var s in Lesson.Steps) Recorder.CompleteStep(Lesson, s.Id);
    }

    private ReviewItemState Item => Recorder.Project(Content).Reviews.Single();

    // --- The event ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_hint_is_recorded_as_a_HintUsed_event_with_its_level()
    {
        Recorder.HintUsed("lesson.l", "step.q", 2);

        var e = Assert.Single(_log.Items);
        var data = JsonDocument.Parse(e.Data!).RootElement;
        Assert.Equal((ProgressEventTypes.HintUsed, "lesson.l", "step.q", 2), (e.Type, e.LessonId, e.StepId, data.GetProperty("level").GetInt32()));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("item").ValueKind);
    }

    [Fact]
    public void A_hint_asked_in_review_names_the_review_item()
    {
        Recorder.HintUsed("lesson.l", "step.q", 1, "review.q");

        Assert.Equal("review.q", JsonDocument.Parse(_log.Items.Single().Data!).RootElement.GetProperty("item").GetString());
    }

    [Fact]
    public void Hints_do_not_change_lesson_progress_or_unlock_anything()
    {
        Recorder.CompleteStep(Lesson, "step.intro");
        var before = Recorder.Project(Content);

        Recorder.HintUsed("lesson.l", "step.q", 1);
        Recorder.HintUsed("lesson.l", "step.q", 2);
        var after = Recorder.Project(Content);

        Assert.Equal(before.Tracks[0].Lessons[0].Progress.CompletedCount, after.Tracks[0].Lessons[0].Progress.CompletedCount);
        Assert.Equal(before.Tracks[0].Lessons[0].Status, after.Tracks[0].Lessons[0].Status);
        Assert.False(after.Reviews.Single().IsUnlocked);
    }

    // --- Rating and schedule -----------------------------------------------------------------------------------------

    private Rating AnswerQuickly(int hints)
    {
        var rating = Recorder.ReviewAnswered(Item, correct: true, hints, TimeSpan.FromSeconds(2));
        return rating;
    }

    [Theory]
    [InlineData(0, Rating.Easy)]
    [InlineData(1, Rating.Hard)]
    [InlineData(2, Rating.Hard)]
    [InlineData(3, Rating.Again)]
    [InlineData(4, Rating.Again)]
    public void Each_rung_of_help_lowers_the_rating_of_the_same_quick_correct_answer(int hints, Rating expected)
    {
        FinishLesson();

        Assert.Equal(expected, AnswerQuickly(hints));
    }

    [Fact]
    public void The_more_help_the_learner_needed_the_sooner_the_item_comes_back()
    {
        var cards = new List<CardState>();
        foreach (var hints in new[] { 0, 1, 3 })
        {
            var run = new HintTests();
            run.FinishLesson();
            run.AnswerQuickly(hints);
            cards.Add(run.Item.Card!);
        }
        var (unaided, nudged, shown) = (cards[0], cards[1], cards[2]);

        Assert.True(unaided.Due > nudged.Due, $"no hints ({unaided.Due:u}) should be later than a nudge ({nudged.Due:u})");
        Assert.Equal(T0.AddDays(1), shown.Due);

        // A first review rounds a Hard and an Again interval to the same day; the memory FSRS keeps still ranks them.
        Assert.True(unaided.Stability > nudged.Stability);
        Assert.True(nudged.Stability > shown.Stability);
    }

    [Fact]
    public void The_hint_count_is_stored_on_the_ReviewAnswered_event()
    {
        FinishLesson();

        AnswerQuickly(2);

        var answer = _log.Items.Single(e => e.Type == ProgressEventTypes.ReviewAnswered);
        Assert.Equal(2, JsonDocument.Parse(answer.Data!).RootElement.GetProperty("hintsUsed").GetInt32());
    }
}
