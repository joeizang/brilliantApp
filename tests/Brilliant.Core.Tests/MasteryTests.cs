using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>
/// Mastery is "what I truly know" as opposed to "what I've merely seen": a concept counts once its lesson is done, and
/// is mastered as its review items prove durable (FSRS stability, discounted by how much has been forgotten since).
/// </summary>
public class MasteryTests
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

    private static ChoiceStep Choice(string id) =>
        new(id, id, "?", false, [new ChoiceOption("a", true, null), new ChoiceOption("b", false, null)]);

    private static readonly WriteCodeStep Write = new("step.a.write", "Write", "?", "python", "", "f", [new CodeTest("1", "1")]);

    // Lesson A: two concepts with a review item each, one concept with none. Lesson B: one concept, two items.
    private static readonly Lesson A = new("lesson.a", "A", "track.t",
        [new ExplainStep("step.a.intro", "intro", "b", [], null), Choice("step.a.one"), Choice("step.a.two"), Write])
    {
        Concepts = [new Concept("concept.one", "One"), new Concept("concept.two", "Two"), new Concept("concept.bare", "Bare")],
        ReviewItems = [new ReviewItem("review.one", "concept.one", "step.a.one"), new ReviewItem("review.two", "concept.two", "step.a.two")],
    };

    private static readonly Lesson B = new("lesson.b", "B", "track.t",
        [new ExplainStep("step.b.intro", "intro", "b", [], null), Choice("step.b.x"), Choice("step.b.y")])
    {
        Concepts = [new Concept("concept.b", "B")],
        ReviewItems = [new ReviewItem("review.b.x", "concept.b", "step.b.x"), new ReviewItem("review.b.y", "concept.b", "step.b.y", ReviewKind.Pattern)],
    };

    private static ContentGraph Graph(params Lesson[] lessons) =>
        new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", lessons.Select(l => l.Id).ToList())], lessons);

    private static readonly ContentGraph Content = Graph(A, B);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    private void Finish(Lesson lesson)
    {
        foreach (var s in lesson.Steps) Recorder.CompleteStep(lesson, s.Id);
    }

    private LearnerState Project(ContentGraph? content = null) =>
        LearnerStateProjector.Project(_log.ReadAll(), content ?? Content, _clock.Now);

    private ConceptMastery Concept(string id, ContentGraph? content = null) => Project(content).Concepts.Single(c => c.Concept.Id == id);

    private void Review(string itemId, Rating rating, bool correct = true)
    {
        var item = Project().Reviews.Single(r => r.Item.Id == itemId).Item;
        var lessonId = Project().Reviews.Single(r => r.Item.Id == itemId).Lesson.Id;
        Recorder.ReviewAnswered(lessonId, item, correct, 0, TimeSpan.FromSeconds(3), rating);
    }

    /// <summary>Answers the item each time it falls due, until the clock has moved <paramref name="times"/> reviews on.</summary>
    private void ReviewOnSchedule(string itemId, int times, Rating rating = Rating.Good)
    {
        for (var i = 0; i < times; i++)
        {
            Review(itemId, rating);
            _clock.Now = Project().Reviews.Single(r => r.Item.Id == itemId).Card!.Due;
        }
    }

    // --- Levels: not started, seen, learning, known -----------------------------------------------------------------------

    [Fact]
    public void A_concept_of_an_unfinished_lesson_is_not_started()
    {
        Recorder.CompleteStep(A, "step.a.intro");

        var c = Concept("concept.one");

        Assert.Equal(ConceptLevel.NotStarted, c.Level);
        Assert.Equal(0, c.Mastery);
    }

    [Fact]
    public void A_finished_lesson_alone_is_seen_not_mastered()
    {
        Finish(A);

        var c = Concept("concept.one");

        Assert.Equal(ConceptLevel.Seen, c.Level);
        Assert.Equal(0, c.Mastery);
    }

    [Fact]
    public void A_concept_with_no_review_items_never_gets_past_seen()
    {
        Finish(A);

        Assert.Equal(ConceptLevel.Seen, Concept("concept.bare").Level);
        Assert.Equal(0, Concept("concept.bare").Mastery);
    }

    [Fact]
    public void One_good_review_starts_learning_but_is_far_from_mastered()
    {
        Finish(A);
        Review("review.one", Rating.Good);

        var c = Concept("concept.one");

        Assert.Equal(ConceptLevel.Learning, c.Level);
        Assert.InRange(c.Mastery, 0.1, 0.2);   // stability 3.17 days of the 21 that count as known
    }

    [Fact]
    public void Mastery_grows_with_each_review_on_schedule_until_the_concept_is_known()
    {
        Finish(A);
        var seen = new List<double>();
        for (var i = 0; i < 8; i++)
        {
            ReviewOnSchedule("review.one", 1);
            seen.Add(Concept("concept.one").Mastery);
        }

        // Sampled as each review falls due, so once stability is saturated it hovers at the retention target (~0.9); allow for rounding noise.
        Assert.All(seen.Zip(seen.Skip(1)), pair => Assert.True(pair.Second >= pair.First - 0.001, $"{pair.First} -> {pair.Second}"));
        Assert.True(seen[^1] > seen[0] + 0.5);
        Assert.Equal(ConceptLevel.Known, Concept("concept.one").Level);
        Assert.InRange(Concept("concept.one").Mastery, MasteryModel.KnownThreshold, 1.0);
    }

    [Fact]
    public void Mastery_never_exceeds_one()
    {
        Finish(A);
        Review("review.one", Rating.Easy);
        for (var i = 0; i < 20; i++) ReviewOnSchedule("review.one", 1, Rating.Easy);

        Assert.True(Concept("concept.one").Mastery <= 1.0);
    }

    [Fact]
    public void Mastery_fades_as_time_passes_without_review()
    {
        Finish(A);
        ReviewOnSchedule("review.one", 4);
        var fresh = Concept("concept.one").Mastery;

        _clock.Now = _clock.Now.AddDays(60);

        Assert.True(Concept("concept.one").Mastery < fresh);
    }

    [Fact]
    public void A_lapse_lowers_mastery()
    {
        Finish(A);
        ReviewOnSchedule("review.one", 4);
        var before = Concept("concept.one").Mastery;

        Review("review.one", Rating.Again, correct: false);

        Assert.True(Concept("concept.one").Mastery < before);
    }

    [Fact]
    public void A_forgotten_concept_is_learning_again_not_known()
    {
        Finish(A);
        ReviewOnSchedule("review.one", 8);
        Assert.Equal(ConceptLevel.Known, Concept("concept.one").Level);

        var card = Project().Reviews.Single(r => r.Item.Id == "review.one").Card!;
        _clock.Now = card.LastReview.AddDays(card.Stability * 20);   // far past the point the memory was expected to last

        Assert.Equal(ConceptLevel.Learning, Concept("concept.one").Level);
    }

    // --- Several items, several concepts ----------------------------------------------------------------------------------

    [Fact]
    public void A_concept_with_two_review_items_averages_them_and_an_unreviewed_item_counts_as_zero()
    {
        Finish(B);
        Review("review.b.x", Rating.Good);
        var one = Concept("concept.b").Mastery;
        Review("review.b.y", Rating.Good);
        var both = Concept("concept.b").Mastery;

        Assert.InRange(one, 0.05, 0.1);        // half of a single-item concept's ~0.15
        Assert.InRange(both, 0.1, 0.2);
        Assert.Equal(2, Concept("concept.b").Items.Count);
    }

    [Fact]
    public void Concepts_of_one_lesson_are_scored_apart()
    {
        Finish(A);
        Review("review.one", Rating.Easy);

        Assert.True(Concept("concept.one").Mastery > 0);
        Assert.Equal(0, Concept("concept.two").Mastery);
    }

    [Fact]
    public void Re_solves_belong_to_no_concept()
    {
        Recorder.CodeSubmitted("lesson.a", "step.a.write", "nope", 0, 1);
        Finish(A);
        Review("resolve.step.a.write", Rating.Easy);

        Assert.All(Project().Concepts, c => Assert.DoesNotContain(c.Items, i => i.Item.Kind == ReviewKind.Resolve));
        Assert.All(Project().Concepts, c => Assert.Equal(0, c.Mastery));
    }

    [Fact]
    public void Every_concept_of_every_lesson_is_listed_in_track_order()
    {
        Assert.Equal(["concept.one", "concept.two", "concept.bare", "concept.b"], Project().Concepts.Select(c => c.Concept.Id));
    }

    // --- Track mastery --------------------------------------------------------------------------------------------------

    [Fact]
    public void Track_mastery_is_the_mean_of_its_concepts_with_unseen_ones_counting_as_zero()
    {
        Finish(A);
        Review("review.one", Rating.Good);
        Review("review.two", Rating.Good);
        var state = Project();
        var perConcept = state.Concepts.Select(c => c.Mastery).ToList();   // one, two, bare, b

        Assert.Equal(4, perConcept.Count);
        Assert.Equal(perConcept.Sum() / 4, state.TrackMastery("track.t"), 10);
        Assert.True(state.TrackMastery("track.t") < state.Concept("concept.one")!.Mastery);
    }

    [Fact]
    public void A_track_with_no_concepts_has_no_mastery_rather_than_a_division_error()
    {
        var empty = Graph(new Lesson("lesson.e", "E", "track.t", [new ExplainStep("step.e", "e", "b", [], null)]));

        Assert.Equal(0, Project(empty).TrackMastery("track.t"));
        Assert.Equal(0, Project(empty).TrackMastery("track.unknown"));
    }

    [Fact]
    public void Track_mastery_counts_only_that_tracks_concepts()
    {
        var other = new Lesson("lesson.o", "O", "track.other", [new ExplainStep("step.o", "o", "b", [], null), Choice("step.o.q")])
        {
            Concepts = [new Concept("concept.o", "O")],
            ReviewItems = [new ReviewItem("review.o", "concept.o", "step.o.q")],
        };
        var two = new ContentGraph(new PackManifest("pack.t", "1.0.0", 1),
            [new Track("track.t", "T", [A.Id, B.Id]), new Track("track.other", "Other", [other.Id])], [A, B, other]);
        foreach (var s in other.Steps) Recorder.CompleteStep(other, s.Id);
        var item = Project(two).Reviews.Single(r => r.Item.Id == "review.o");
        Recorder.ReviewAnswered("lesson.o", item.Item, true, 0, TimeSpan.FromSeconds(3), Rating.Good);

        var state = Project(two);

        Assert.Equal(["concept.o"], state.ConceptsOf("track.other").Select(c => c.Concept.Id));
        Assert.Equal(state.Concept("concept.o")!.Mastery, state.TrackMastery("track.other"), 10);
        Assert.Equal(0, state.TrackMastery("track.t"));
    }

    [Fact]
    public void Reviews_recorded_before_their_lesson_is_finished_do_not_count_yet()
    {
        // e.g. events synced from another device ahead of the lesson's own completion
        Recorder.ReviewAnswered("lesson.a", A.ReviewItems[0], true, 0, TimeSpan.FromSeconds(3), Rating.Easy);

        var c = Concept("concept.one");

        Assert.Equal(ConceptLevel.NotStarted, c.Level);
        Assert.Equal(0, c.Mastery);
    }

    // --- Accuracy and history --------------------------------------------------------------------------------------------

    [Fact]
    public void Accuracy_counts_lesson_attempts_and_reviews_of_the_concepts_questions()
    {
        _clock.Now = T0;
        Recorder.StepAnswered("lesson.a", "step.a.one", false, [1]);
        _clock.Now = T0.AddMinutes(1);
        Recorder.StepAnswered("lesson.a", "step.a.one", true, [0]);
        Finish(A);
        _clock.Now = T0.AddDays(1);
        Review("review.one", Rating.Good);
        _clock.Now = T0.AddDays(5);
        Review("review.one", Rating.Again, correct: false);

        var c = Concept("concept.one");

        Assert.Equal((2, 1), (c.LessonAttempts, c.LessonCorrect));
        Assert.Equal((2, 1), (c.ReviewAttempts, c.ReviewCorrect));
        Assert.Equal(0.5, c.Accuracy);
    }

    [Fact]
    public void Accuracy_is_unknown_until_the_concept_has_been_attempted()
    {
        Finish(A);

        Assert.Null(Concept("concept.one").Accuracy);
        Assert.Empty(Concept("concept.one").History);
    }

    [Fact]
    public void History_lists_attempts_newest_first_and_says_where_each_happened()
    {
        Recorder.StepAnswered("lesson.a", "step.a.one", true, [0]);
        _clock.Now = T0.AddDays(1);
        Finish(A);
        Review("review.one", Rating.Good, correct: true);

        var history = Concept("concept.one").History;

        Assert.Equal([AttemptSource.Review, AttemptSource.Lesson], history.Select(h => h.Source));
        Assert.Equal([T0.AddDays(1), T0], history.Select(h => h.At));
        Assert.All(history, h => Assert.True(h.Correct));
    }

    [Fact]
    public void A_code_submission_counts_by_whether_it_passed_every_test()
    {
        var code = new Lesson("lesson.c", "C", "track.t", [Write with { Id = "step.c.write" }])
        {
            Concepts = [new Concept("concept.code", "Code")],
            ReviewItems = [new ReviewItem("review.code", "concept.code", "step.c.write")],
        };
        var content = Graph(code);
        Recorder.CodeSubmitted("lesson.c", "step.c.write", "bad", 0, 1);
        Recorder.CodeSubmitted("lesson.c", "step.c.write", "good", 1, 1);

        var c = Concept("concept.code", content);

        Assert.Equal((2, 1), (c.LessonAttempts, c.LessonCorrect));
    }

    [Fact]
    public void Answers_to_other_steps_and_other_lessons_do_not_count()
    {
        Recorder.StepAnswered("lesson.a", "step.a.two", false, [1]);
        Recorder.StepAnswered("lesson.b", "step.a.one", false, [1]);

        Assert.Equal(0, Concept("concept.one").LessonAttempts);
    }

    // --- Derived, never stored ------------------------------------------------------------------------------------------

    [Fact]
    public void Duplicate_events_and_event_order_change_nothing()
    {
        Finish(A);
        Review("review.one", Rating.Good);
        Recorder.StepAnswered("lesson.a", "step.a.one", true, [0]);
        var events = _log.ReadAll().ToList();
        var expected = LearnerStateProjector.Project(events, Content, _clock.Now).Concepts;

        var shuffled = events.Concat(events).Reverse().ToList();
        var actual = LearnerStateProjector.Project(shuffled, Content, _clock.Now).Concepts;

        Assert.Equal(expected.Select(c => (c.Concept.Id, c.Mastery, c.Level, c.LessonAttempts, c.ReviewAttempts)),
                     actual.Select(c => (c.Concept.Id, c.Mastery, c.Level, c.LessonAttempts, c.ReviewAttempts)));
    }

    [Fact]
    public void A_deleted_question_step_takes_its_review_history_with_it()
    {
        Finish(A);
        Review("review.one", Rating.Good);
        Assert.True(Concept("concept.one").Mastery > 0);

        var trimmed = A with { Steps = A.Steps.Where(s => s.Id != "step.a.one").ToList() };

        var c = Concept("concept.one", Graph(trimmed, B));

        Assert.Empty(c.Items);
        Assert.Equal(0, c.Mastery);
        Assert.Equal(0, c.ReviewAttempts);
    }

    [Fact]
    public void Looking_up_a_concept_by_id_returns_null_when_there_is_none()
    {
        Assert.Null(Project().Concept("concept.nope"));
        Assert.NotNull(Project().Concept("concept.one"));
    }
}
