using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>The per-lesson test-out (#146): which questions it asks, what a perfect attempt records, and what that does to the schedule and mastery.</summary>
public class LessonTestOutTests
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
        public bool Append(ProgressEvent e) { if (Items.Any(i => i.Id == e.Id)) return false; Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static ChoiceStep Choice(string id) => new(id, id, "?", false, [new ChoiceOption("a", true, null), new ChoiceOption("b", false, null)]);
    private static PredictOutputStep Predict(string id) => new(id, id, "?", new CodeSnippet("python", "print(1)"), ["1"], [], []);
    private static FillBlankStep Fill(string id) => new(id, id, "?", "python", "x = {{b}}", [new Blank("b", ["1"], [])]);
    private static ParsonsStep Parsons(string id) => new(id, id, "?", "python", [new ParsonsLine("a", 0), new ParsonsLine("b", 0)]);
    private static WriteCodeStep Write(string id) => new(id, id, "?", "python", "def f(): pass", "f", [new CodeTest("", "1")]);

    /// <summary>A lesson with an intro and one review item per given question step.</summary>
    private static Lesson Lesson(string name, params Step[] questions) =>
        new($"lesson.{name}", name, "track.t", [new ExplainStep($"step.{name}.intro", "intro", "b", [], null), .. questions])
        {
            Concepts = [new Concept($"concept.{name}", name)],
            ReviewItems = questions.Select(q => new ReviewItem($"review.{q.Id}", $"concept.{name}", q.Id)).ToList(),
        };

    private static Lesson FourChoices(string name) =>
        Lesson(name, Choice($"{name}.q1"), Choice($"{name}.q2"), Choice($"{name}.q3"), Choice($"{name}.q4"));

    private static ContentGraph Graph(params Lesson[] lessons) => new(
        new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", lessons.Select(l => l.Id).ToList())], lessons);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    private LearnerState Project(ContentGraph content, DateTimeOffset? now = null) =>
        LearnerStateProjector.Project(_log.ReadAll(), content, now ?? _clock.Now);

    // --- Question selection ----------------------------------------------------------------------------------------

    [Fact]
    public void A_lesson_with_four_or_more_review_items_is_asked_four_questions()
    {
        var lesson = Lesson("big", Choice("a"), Choice("b"), Choice("c"), Choice("d"), Choice("e"));

        Assert.Equal(4, LessonTestOut.Select(lesson).Count);
    }

    [Fact]
    public void A_lesson_with_three_review_items_is_asked_all_three()
    {
        var lesson = Lesson("three", Choice("a"), Choice("b"), Choice("c"));

        Assert.Equal(["review.a", "review.b", "review.c"], LessonTestOut.Select(lesson).Select(i => i.Id));
    }

    [Fact]
    public void A_lesson_with_fewer_than_three_askable_items_is_not_tested_out()
    {
        Assert.Empty(LessonTestOut.Select(Lesson("two", Choice("a"), Choice("b"))));
        Assert.Empty(LessonTestOut.Select(Lesson("none")));
    }

    [Fact]
    public void Items_whose_question_is_missing_or_cannot_be_asked_do_not_count()
    {
        var lesson = Lesson("odd", Choice("a"), Choice("b")) with
        {
            ReviewItems = [.. Lesson("odd", Choice("a"), Choice("b")).ReviewItems, new ReviewItem("review.gone", "concept.odd", "step.deleted")],
        };

        Assert.Empty(LessonTestOut.Select(lesson));
    }

    [Fact]
    public void The_hardest_questions_are_chosen_and_asked_in_lesson_order()
    {
        var lesson = Lesson("mixed", Choice("c1"), Predict("p1"), Choice("c2"), Parsons("s1"), Fill("f1"), Choice("c3"));

        // Parsons, fill-in-the-blank and predict-output are harder than a choice; the lesson's own order decides among the rest, and the questions come back in that order.
        Assert.Equal(["review.c1", "review.p1", "review.s1", "review.f1"], LessonTestOut.Select(lesson).Select(i => i.Id));
    }

    [Fact]
    public void Writing_code_counts_as_the_hardest_question()
    {
        var lesson = Lesson("code", Choice("c1"), Choice("c2"), Choice("c3"), Predict("p1"), Write("w1"));

        Assert.Contains("review.w1", LessonTestOut.Select(lesson).Select(i => i.Id));
        Assert.DoesNotContain("review.c3", LessonTestOut.Select(lesson).Select(i => i.Id));
    }

    [Fact]
    public void Selection_is_deterministic()
    {
        var lesson = Lesson("same", Choice("a"), Choice("b"), Choice("c"), Choice("d"), Choice("e"), Choice("f"));

        Assert.Equal(LessonTestOut.Select(lesson), LessonTestOut.Select(lesson));
    }

    [Fact]
    public void Only_concept_items_are_asked()
    {
        var lesson = FourChoices("k") with { ReviewItems = FourChoices("k").ReviewItems.Select(i => i with { Kind = ReviewKind.Pattern }).ToList() };

        Assert.Empty(LessonTestOut.Select(lesson));
    }

    // --- Offering ----------------------------------------------------------------------------------------------------

    [Fact]
    public void It_is_offered_on_an_unstarted_lesson_with_enough_items()
    {
        var lesson = FourChoices("o");

        Assert.True(LessonTestOut.IsOffered(Project(Graph(lesson)).Track("track.t")!.Lessons.Single().Progress));
    }

    [Fact]
    public void It_is_not_offered_on_a_lesson_without_enough_items()
    {
        var lesson = Lesson("thin", Choice("a"), Choice("b"));

        Assert.False(LessonTestOut.IsOffered(Project(Graph(lesson)).Track("track.t")!.Lessons.Single().Progress));
    }

    [Fact]
    public void It_is_not_offered_once_the_lesson_is_started_or_complete()
    {
        var lesson = FourChoices("s");
        Recorder.CompleteStep(lesson, "step.s.intro");
        Assert.False(LessonTestOut.IsOffered(Project(Graph(lesson)).Track("track.t")!.Lessons.Single().Progress));

        var done = FourChoices("d");
        foreach (var s in done.Steps) Recorder.CompleteStep(done, s.Id);
        Assert.False(LessonTestOut.IsOffered(Project(Graph(done)).Track("track.t")!.Lessons.Single().Progress));
    }

    // --- The event ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Testing_out_records_a_lesson_level_event_naming_the_questions_asked()
    {
        var lesson = FourChoices("e");

        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));

        var e = Assert.Single(_log.Items);
        Assert.Equal(ProgressEventTypes.LessonTestedOut, e.Type);
        Assert.Equal(lesson.Id, e.LessonId);
        Assert.Equal("", e.StepId);
        using var doc = JsonDocument.Parse(e.Data!);
        Assert.Equal(4, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    // --- Projection --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_test_out_completes_the_lesson_and_unlocks_the_next()
    {
        var one = FourChoices("one");
        var two = FourChoices("two");
        Recorder.LessonTestedOut(one, LessonTestOut.Select(one));

        var lessons = Project(Graph(one, two)).Track("track.t")!.Lessons;

        Assert.Equal(LessonStatus.Completed, lessons[0].Status);
        Assert.Equal(LessonStatus.Available, lessons[1].Status);
        Assert.True(lessons[0].Progress.TestedOut);
    }

    [Fact]
    public void A_tested_out_lesson_has_no_steps_to_play_and_none_shown_as_new()
    {
        var lesson = FourChoices("n");
        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));

        var progress = Project(Graph(lesson)).Track("track.t")!.Lessons.Single().Progress;

        Assert.Null(progress.NextStep);
        Assert.Null(progress.CurrentStep);
        Assert.Empty(progress.NewSteps);
        Assert.Equal(0, progress.CompletedCount);
    }

    [Fact]
    public void A_test_out_adds_no_step_to_the_days_goal()
    {
        var lesson = FourChoices("g");
        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));

        Assert.Equal(0, Project(Graph(lesson)).Today.DoneToday);
    }

    [Fact]
    public void Every_review_item_of_the_lesson_is_seeded_with_a_long_interval()
    {
        var lesson = FourChoices("seed");
        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));

        var reviews = Project(Graph(lesson)).Reviews;

        Assert.Equal(4, reviews.Count);
        Assert.All(reviews, r =>
        {
            Assert.True(r.IsUnlocked);
            Assert.NotNull(r.Card);
            Assert.False(r.IsNew);
            Assert.Equal(0, r.Answers);
            Assert.True(r.Card!.Stability >= 21, "stability should reach the mastery model's 'fully learned' mark");
            Assert.True(r.Card.Due - T0 >= TimeSpan.FromDays(21), "the first review should be weeks away");
        });
    }

    [Fact]
    public void Seeded_items_are_not_due_so_the_review_queue_stays_empty()
    {
        var lesson = FourChoices("q");
        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));

        Assert.Empty(Project(Graph(lesson), T0.AddDays(1)).Queue.Items);
    }

    [Fact]
    public void Seeded_items_come_due_after_their_long_interval()
    {
        var lesson = FourChoices("due");
        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));

        Assert.Equal(4, Project(Graph(lesson), T0.AddDays(60)).Queue.Items.Count);
    }

    [Fact]
    public void Mastery_shows_the_lesson_s_concepts_as_learned()
    {
        var lesson = FourChoices("m");
        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));

        var concept = Project(Graph(lesson)).Concepts.Single();

        Assert.Equal(ConceptLevel.Known, concept.Level);
        Assert.True(concept.Mastery >= MasteryModel.KnownThreshold);
    }

    [Fact]
    public void Mastery_fades_like_any_other_when_the_seeded_items_are_left_alone()
    {
        var lesson = FourChoices("fade");
        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));

        var fresh = Project(Graph(lesson)).Concepts.Single().Mastery;
        var later = Project(Graph(lesson), T0.AddDays(120)).Concepts.Single().Mastery;

        Assert.True(later < fresh);
    }

    [Fact]
    public void A_review_answer_after_the_test_out_continues_from_the_seeded_schedule()
    {
        var lesson = FourChoices("cont");
        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));
        var seeded = Project(Graph(lesson)).Reviews[0];

        _clock.Now = T0.AddDays(40);
        Recorder.ReviewAnswered(lesson.Id, seeded.Item, correct: true, hintsUsed: 0, TimeSpan.FromSeconds(5), Rating.Good);
        var after = Project(Graph(lesson), _clock.Now).Reviews.Single(r => r.Item.Id == seeded.Item.Id);

        Assert.Equal(1, after.Answers);
        Assert.True(after.Card!.Stability > seeded.Card!.Stability, "a recalled item grows stronger than its seed");
        Assert.Equal(seeded.Card.Reps + 1, after.Card.Reps);
    }

    [Fact]
    public void A_failing_review_answer_after_the_test_out_still_lapses_the_item()
    {
        var lesson = FourChoices("lapse");
        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));
        var item = lesson.ReviewItems[0];

        _clock.Now = T0.AddDays(40);
        Recorder.ReviewAnswered(lesson.Id, item, correct: false, hintsUsed: 0, TimeSpan.FromSeconds(5), Rating.Again);
        var after = Project(Graph(lesson), _clock.Now).Reviews.Single(r => r.Item.Id == item.Id);

        Assert.Equal(1, after.Card!.Lapses);
        Assert.True(after.Card.Stability < 21);
    }

    [Fact]
    public void A_test_out_never_overwrites_an_item_that_already_has_a_schedule()
    {
        var lesson = FourChoices("pre");
        var item = lesson.ReviewItems[0];
        Recorder.ReviewAnswered(lesson.Id, item, correct: true, hintsUsed: 0, TimeSpan.FromSeconds(5), Rating.Good);
        var before = Project(Graph(lesson)).Reviews.Single(r => r.Item.Id == item.Id).Card;

        _clock.Now = T0.AddMinutes(5);
        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));
        var after = Project(Graph(lesson), T0).Reviews.Single(r => r.Item.Id == item.Id).Card;

        Assert.Equal(before, after);
    }

    [Fact]
    public void A_missed_test_out_records_nothing_so_nothing_changes()
    {
        var lesson = FourChoices("miss");
        var graph = Graph(lesson);
        var before = LearnerStateFingerprint.Of(_log.ReadAll(), graph, T0);

        // The UI records nothing on a miss, so the learner's state is exactly what it was and the lesson plays from its first step.
        Assert.Empty(_log.Items);
        Assert.Equal(before, LearnerStateFingerprint.Of(_log.ReadAll(), graph, T0));
        Assert.Equal("step.miss.intro", Project(graph).Track("track.t")!.Lessons.Single().Progress.NextStep!.Id);
        Assert.All(Project(graph).Reviews, r => Assert.Null(r.Card));
    }

    [Fact]
    public void The_projection_ignores_event_order_and_duplicates()
    {
        var lesson = FourChoices("order");
        var other = FourChoices("other");
        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));
        _clock.Now = T0.AddHours(1);
        Recorder.CompleteStep(other, "step.other.intro");
        var graph = Graph(lesson, other);
        var events = _log.ReadAll().ToList();

        var forward = LearnerStateFingerprint.Of(events, graph, T0.AddDays(2));
        var shuffled = LearnerStateFingerprint.Of([.. events.AsEnumerable().Reverse(), .. events], graph, T0.AddDays(2));

        Assert.Equal(forward, shuffled);
    }

    [Fact]
    public void A_lesson_content_update_does_not_record_a_completion_for_a_tested_out_lesson()
    {
        var lesson = FourChoices("upd");
        Recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));

        Assert.Equal(0, Recorder.RecordCompletionsFrom(Graph(lesson)));
        Assert.DoesNotContain(_log.Items, e => e.Type == ProgressEventTypes.LessonCompleted);
    }
}
