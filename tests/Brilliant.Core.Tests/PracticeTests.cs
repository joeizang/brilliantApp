using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Practice;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>
/// Practice core (#36, slice 2): PracticeAnswered events feeding derived <c>pick.</c> and <c>resolve.</c> review items, pattern mastery,
/// and the Practice Selector.
/// </summary>
public class PracticeTests
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

    private static WriteCodeStep Problem(string id, string? pattern) =>
        new(id, id, "?", "python", "", "f", [new CodeTest("1", "1")]) { Pattern = pattern };

    /// <summary>A lesson teaching pattern.{name}, with a concept question and two problems tagged with it (plus any extra steps).</summary>
    private static Lesson Teaching(string name, params Step[] extra)
    {
        var question = new ChoiceStep($"step.{name}.q", "Q", "?", false, [new ChoiceOption("a", true, null), new ChoiceOption("b", false, null)]);
        var steps = new List<Step> { question, Problem($"step.{name}.p1", $"pattern.{name}"), Problem($"step.{name}.p2", $"pattern.{name}") };
        steps.AddRange(extra);
        return new Lesson($"lesson.{name}", name, "track.t", steps)
        {
            Concepts = [new Concept($"concept.{name}", name)],
            ReviewItems = [new ReviewItem($"review.{name}.q", $"concept.{name}", question.Id)],
            Patterns = [new Pattern($"pattern.{name}", $"Pattern {name}")],
        };
    }

    private static readonly Lesson A = Teaching("a");
    private static readonly Lesson B = Teaching("b", Problem("step.b.drill", null));
    private static readonly Lesson C = Teaching("c");

    // D's last problem uses A's pattern, which an earlier lesson taught.
    private static readonly Lesson D = Teaching("d", Problem("step.d.uses-a", "pattern.a"));

    private static ContentGraph Graph(params Lesson[] lessons)
    {
        lessons = lessons.Length == 0 ? [A, B, C, D] : lessons;
        return new ContentGraph(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", lessons.Select(l => l.Id).ToList())], lessons);
    }

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    private void Finish(params Lesson[] lessons)
    {
        foreach (var lesson in lessons)
        foreach (var s in lesson.Steps)
            Recorder.CompleteStep(lesson, s.Id);
    }

    private LearnerState Project(ContentGraph? content = null, DateTimeOffset? now = null) =>
        LearnerStateProjector.Project(_log.ReadAll(), content ?? Graph(), now ?? _clock.Now);

    private Rating Pick(Lesson lesson, string stepId, string? picked, string run = "run.1", int seconds = 20, PracticeMode mode = PracticeMode.Practice)
    {
        var step = lesson.AllSteps().Single(s => s.Id == stepId);
        return Recorder.PracticePicked(lesson.Id, stepId, picked, picked == step.Pattern, TimeSpan.FromSeconds(seconds), mode, run);
    }

    private Rating Solve(Lesson lesson, string stepId, bool correct, string run = "run.1", int hints = 0, int seconds = 120)
    {
        var step = lesson.AllSteps().Single(s => s.Id == stepId);
        return Recorder.PracticeSolved(lesson.Id, step, stepId, correct, hints, TimeSpan.FromSeconds(seconds), PracticeMode.Practice, run);
    }

    private static ReviewItemState? Item(LearnerState state, string id) => state.Reviews.SingleOrDefault(r => r.Item.Id == id);

    // --- The event ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pick_is_recorded_with_the_pattern_chosen_and_round_trips()
    {
        Finish(A, B, C);
        var rating = Pick(A, "step.a.p1", "pattern.b", seconds: 30);

        var e = _log.Items.Last();
        Assert.Equal(ProgressEventTypes.PracticeAnswered, e.Type);
        Assert.Equal((A.Id, "step.a.p1"), (e.LessonId, e.StepId));
        var answer = PracticeAnswer.From(e)!;
        Assert.Equal(new PracticeAnswer("step.a.p1", PracticePart.Pick, false, "pattern.b", 0, TimeSpan.FromSeconds(30), Rating.Again,
            PracticeMode.Practice, "run.1") { Day = AnswerDay.Today(_clock) }, answer);
        Assert.Equal(Rating.Again, rating);
        Assert.Equal("pick.step.a.p1", answer.ItemId);
    }

    [Fact]
    public void A_pick_never_made_is_wrong_with_no_pattern()
    {
        Finish(A, B, C);
        var rating = Recorder.PracticePicked(A.Id, "step.a.p1", null, correct: true, TimeSpan.Zero, PracticeMode.Interview, "run.9");

        var answer = PracticeAnswer.From(_log.Items.Last())!;
        Assert.Equal(Rating.Again, rating);
        Assert.False(answer.Correct);
        Assert.Null(answer.Picked);
        Assert.Equal(PracticeMode.Interview, answer.Mode);
        Assert.Contains("\"picked\":null", _log.Items.Last().Data);
    }

    [Theory]
    [InlineData(true, 1, Rating.Hard)]
    [InlineData(true, 3, Rating.Again)]
    [InlineData(false, 0, Rating.Again)]
    public void Hints_on_a_pick_count_as_in_Review(bool correct, int hints, Rating expected) =>
        Assert.Equal(expected, RatingInference.InferPick(correct, TimeSpan.FromSeconds(5), hints));

    [Theory]
    [InlineData(10, Rating.Easy)]
    [InlineData(30, Rating.Good)]
    [InlineData(60, Rating.Hard)]
    public void A_right_pick_is_rated_by_time(int seconds, Rating expected)
    {
        Finish(A, B, C);
        Assert.Equal(expected, Pick(A, "step.a.p1", "pattern.a", seconds: seconds));
    }

    [Fact]
    public void A_solve_is_rated_like_a_review_of_the_form_solved()
    {
        Finish(A, B, C);
        Assert.Equal(Rating.Hard, Solve(A, "step.a.p1", true, hints: 1));
        Assert.Equal(Rating.Again, Solve(A, "step.a.p2", false));
        var answer = PracticeAnswer.From(_log.Items.Last())!;
        Assert.Equal((PracticePart.Solve, (string?)null), (answer.Part, answer.Picked));
        Assert.Equal("resolve.step.a.p2", answer.ItemId);
    }

    [Theory]
    [InlineData("""{"step":"step.a.p1","part":"pick","correct":true,"rating":3}""", true)]
    [InlineData("""{"step":"step.other","part":"pick","correct":true,"rating":3}""", false)]
    [InlineData("""{"step":"step.a.p1","part":"guess","correct":true,"rating":3}""", false)]
    [InlineData("""{"step":"step.a.p1","part":"solve","correct":"yes","rating":3}""", false)]
    [InlineData("""{"step":"step.a.p1","part":"solve","correct":true,"rating":5}""", false)]
    [InlineData("""{"step":"step.a.p1","part":"solve","correct":true}""", false)]
    [InlineData("""[1]""", false)]
    [InlineData("""not json""", false)]
    public void Malformed_practice_answers_are_ignored(string data, bool valid)
    {
        var e = new ProgressEvent("e1", "d", T0, ProgressEventTypes.PracticeAnswered, A.Id, "step.a.p1", data);
        Assert.Equal(valid, PracticeAnswer.From(e) is not null);
    }

    // --- Derived review items ----------------------------------------------------------------------------------------

    [Fact]
    public void Practising_creates_a_pick_item_and_a_re_solve_and_schedules_them()
    {
        Finish(A, B, C);
        Assert.Null(Item(Project(), "pick.step.a.p1"));
        Assert.Null(Item(Project(), "resolve.step.a.p1"));

        Pick(A, "step.a.p1", "pattern.a");
        _clock.Now = T0.AddMinutes(2);
        Solve(A, "step.a.p1", true);

        var state = Project();
        var pick = Item(state, "pick.step.a.p1")!;
        var resolve = Item(state, "resolve.step.a.p1")!;
        Assert.Equal((ReviewKind.Pattern, "", 1, Rating.Good), (pick.Item.Kind, pick.Item.ConceptId, pick.Answers, pick.LastRating));
        Assert.Equal((ReviewKind.Resolve, 1, Rating.Good), (resolve.Item.Kind, resolve.Answers, resolve.LastRating));
        Assert.Equal(FsrsScheduler.Schedule(null, Rating.Good, T0), pick.Card);
        Assert.True(pick.IsUnlocked);
        Assert.Same(A.Steps[1], pick.Question);
    }

    [Fact]
    public void A_pick_alone_creates_no_re_solve()
    {
        Finish(A, B, C);
        Pick(A, "step.a.p1", "pattern.a");
        Assert.NotNull(Item(Project(), "pick.step.a.p1"));
        Assert.Null(Item(Project(), "resolve.step.a.p1"));
    }

    [Fact]
    public void Only_the_first_answer_to_a_part_in_a_run_is_scheduled()
    {
        Finish(A, B, C);
        Solve(A, "step.a.p1", false, run: "run.1");
        _clock.Now = T0.AddMinutes(1);
        Solve(A, "step.a.p1", true, run: "run.1");    // a retry in the same visit
        Pick(A, "step.a.p1", "pattern.a", run: "run.1");

        var resolve = Item(Project(), "resolve.step.a.p1")!;
        Assert.Equal((1, Rating.Again), (resolve.Answers, resolve.LastRating));

        _clock.Now = T0.AddDays(1);
        Solve(A, "step.a.p1", true, run: "run.2");     // a later visit counts
        Assert.Equal(2, Item(Project(), "resolve.step.a.p1")!.Answers);
    }

    [Fact]
    public void Review_and_practice_answers_share_one_schedule()
    {
        Finish(A, B, C);
        Pick(A, "step.a.p1", "pattern.a");
        _clock.Now = T0.AddDays(3);
        var pick = Item(Project(), "pick.step.a.p1")!;
        Recorder.ReviewAnswered(pick, correct: false, hintsUsed: 0, TimeSpan.FromSeconds(5));

        var after = Item(Project(), "pick.step.a.p1")!;
        Assert.Equal((2, Rating.Again), (after.Answers, after.LastRating));
        Assert.Equal(FsrsScheduler.Schedule(FsrsScheduler.Schedule(null, Rating.Good, T0), Rating.Again, T0.AddDays(3)), after.Card);
    }

    [Theory]
    [InlineData(ScreenClass.Desktop)]
    [InlineData(ScreenClass.Phone)]
    public void A_pick_item_in_Review_is_rated_as_a_pick_whatever_form_the_problem_takes(ScreenClass screen)
    {
        var tap = new ParsonsStep("step.a.p1.tap", "Order", "?", "python", [new ParsonsLine("x = 1", 0)]);
        var phoneFriendly = A with
        {
            Steps = A.Steps.Select(s => s.Id == "step.a.p1" ? Problem(s.Id, "pattern.a") with { PhoneAlternative = tap } : s).ToList(),
        };
        var graph = Graph(phoneFriendly, B, C, D);
        Finish(phoneFriendly, B, C);
        Pick(phoneFriendly, "step.a.p1", "pattern.a");

        _clock.Now = T0.AddDays(5);
        var pick = LearnerStateProjector.Project(_log.ReadAll(), graph, _clock.Now, screen: screen).Queue.Items.Single(i => i.Item.Id == "pick.step.a.p1");
        Assert.True(pick.IsPracticePick);
        Assert.Equal(screen == ScreenClass.Phone ? tap : phoneFriendly.Steps[1], pick.Presented);

        // 75 s is fast for writing code or ordering lines, but slow for naming a pattern.
        Assert.Equal(Rating.Hard, Recorder.ReviewAnswered(pick, correct: true, hintsUsed: 0, TimeSpan.FromSeconds(75)));
        Assert.Equal(Rating.Hard, Item(Project(graph), "pick.step.a.p1")!.LastRating);
        Assert.Equal(Rating.Easy, Recorder.ReviewAnswered(pick, correct: true, hintsUsed: 0, TimeSpan.FromSeconds(10)));
        Assert.Equal(Rating.Again, Recorder.ReviewAnswered(pick, correct: false, hintsUsed: 0, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Authored_pattern_questions_and_re_solves_keep_their_own_rating_rule()
    {
        var authored = A with { ReviewItems = [.. A.ReviewItems, new ReviewItem("review.a.which", "concept.a", "step.a.q", ReviewKind.Pattern)] };
        var graph = Graph(authored, B, C, D);
        Finish(authored, B, C);
        Solve(authored, "step.a.p1", true);

        var state = Project(graph);
        var which = Item(state, "review.a.which")!;
        var resolve = Item(state, "resolve.step.a.p1")!;
        Assert.False(which.IsPracticePick);
        Assert.False(resolve.IsPracticePick);
        Assert.Equal(Rating.Hard, Recorder.ReviewAnswered(which, correct: true, hintsUsed: 0, TimeSpan.FromSeconds(40)));   // choice: slow from 30 s
        Assert.Equal(Rating.Easy, Recorder.ReviewAnswered(resolve, correct: true, hintsUsed: 0, TimeSpan.FromSeconds(75))); // write-code: fast to 90 s
    }

    [Fact]
    public void Practice_changes_no_lesson_progress_goal_review_cap_or_concept()
    {
        Finish(A);
        var before = Project();

        Pick(A, "step.a.p1", "pattern.a");
        Solve(A, "step.a.p1", true);
        Solve(B, "step.b.p1", true);   // B isn't even started

        var after = Project();
        Assert.Equal(before.Tracks.SelectMany(t => t.Lessons).Select(l => (l.Status, l.Progress.CompletedCount)),
            after.Tracks.SelectMany(t => t.Lessons).Select(l => (l.Status, l.Progress.CompletedCount)));
        Assert.Equal(before.Today.DoneToday, after.Today.DoneToday);
        Assert.Equal(0, after.Queue.DoneToday);
        Assert.Equal(before.Concepts.Select(c => c.Mastery), after.Concepts.Select(c => c.Mastery));
        Assert.False(Item(after, "resolve.step.b.p1")!.IsUnlocked);
    }

    [Fact]
    public void A_pick_item_retires_when_its_step_loses_its_tag()
    {
        Finish(A, B, C);
        Pick(A, "step.a.p1", "pattern.a");
        Solve(A, "step.a.p1", true);

        var untagged = A with { Steps = A.Steps.Select(s => s.Id == "step.a.p1" ? Problem(s.Id, null) : s).ToList() };
        var state = Project(Graph(untagged, B, C, D));
        Assert.Null(Item(state, "pick.step.a.p1"));
        Assert.NotNull(Item(state, "resolve.step.a.p1"));
    }

    [Fact]
    public void Practice_hints_are_not_lesson_hints()
    {
        Finish(A, B, C);
        Recorder.HintUsed(A.Id, "step.a.p1", 2, "resolve.step.a.p1");
        Solve(A, "step.a.p1", true, hints: 2);

        var resolve = Item(Project(), "resolve.step.a.p1")!;
        Assert.Equal((0, 0), (resolve.LessonHints, resolve.AttemptHints));
        Assert.Equal(Rating.Hard, resolve.LastRating);
    }

    [Fact]
    public void Practice_replays_the_same_in_any_order_and_with_duplicates()
    {
        Finish(A, B, C);
        Pick(A, "step.a.p1", "pattern.a");
        Solve(A, "step.a.p1", false);
        _clock.Now = T0.AddDays(2);
        Pick(B, "step.b.p2", "pattern.c", run: "run.2");
        Solve(B, "step.b.p2", true, run: "run.2");

        var expected = LearnerStateFingerprint.Of(_log.Items, Graph(), T0.AddDays(3));
        var shuffled = _log.Items.AsEnumerable().Reverse().Concat(_log.Items.Take(3)).ToList();
        Assert.Equal(expected, LearnerStateFingerprint.Of(shuffled, Graph(), T0.AddDays(3)));
    }

    // --- Pattern mastery ---------------------------------------------------------------------------------------------

    [Fact]
    public void Patterns_are_not_started_until_their_lesson_is_completed()
    {
        var state = Project();
        Assert.Equal(["pattern.a", "pattern.b", "pattern.c", "pattern.d"], state.Patterns.Select(p => p.Pattern.Id));
        Assert.All(state.Patterns, p => Assert.Equal((0.0, ConceptLevel.NotStarted, false, 0), (p.Mastery, p.Level, p.IsLearned, p.StepsTagged)));
    }

    [Fact]
    public void A_learned_pattern_is_seen_until_practised()
    {
        Finish(A);
        var a = Project().Pattern("pattern.a")!;
        Assert.Equal((0.0, ConceptLevel.Seen, true, 2, 0), (a.Mastery, a.Level, a.IsLearned, a.StepsTagged, a.StepsPractised));
        Assert.Equal(["step.a.p1", "step.a.p2"], a.Steps.Select(s => s.Step.Id));
    }

    [Fact]
    public void A_pattern_averages_its_problems_and_a_problem_its_two_parts()
    {
        Finish(A);
        Pick(A, "step.a.p1", "pattern.a", seconds: 5);
        Solve(A, "step.a.p1", true, seconds: 30);

        var now = T0.AddDays(1);
        var state = Project(now: now);
        var a = state.Pattern("pattern.a")!;
        var p1 = a.Steps[0];
        var expected = (MasteryModel.OfItem(Item(state, "pick.step.a.p1")!.Card, now) + MasteryModel.OfItem(Item(state, "resolve.step.a.p1")!.Card, now)) / 2;
        Assert.Equal(expected, p1.Mastery, 12);
        Assert.True(p1.Mastery > 0);
        Assert.Equal(0, a.Steps[1].Mastery);
        Assert.Equal(p1.Mastery / 2, a.Mastery, 12);
        Assert.Equal((ConceptLevel.Learning, 1), (a.Level, a.StepsPractised));
        Assert.Equal(T0, p1.LastPractised);
    }

    [Fact]
    public void A_pattern_counts_problems_from_later_completed_lessons_only()
    {
        Finish(A, B, C);
        Assert.Equal(2, Project().Pattern("pattern.a")!.StepsTagged);   // D, which also uses it, isn't done

        Finish(D);
        Assert.Equal(["step.a.p1", "step.a.p2", "step.d.uses-a"], Project().Pattern("pattern.a")!.Steps.Select(s => s.Step.Id));
    }

    [Fact]
    public void Practice_does_not_move_concept_mastery()
    {
        Finish(A);
        Pick(A, "step.a.p1", "pattern.a");
        Solve(A, "step.a.p1", true);
        Assert.Equal(0, Project().Concept("concept.a")!.Mastery);
    }

    // --- The Practice Selector --------------------------------------------------------------------------------------

    [Fact]
    public void Practice_needs_three_learned_patterns()
    {
        Finish(A, B);
        Assert.Equal(new PracticeAvailability(2, 4), PracticeSelector.Availability(Project()));
        Assert.Null(PracticeSelector.Next(Project(), new Random(1)));

        Finish(C);
        Assert.True(PracticeSelector.Availability(Project()).IsAvailable);
        Assert.NotNull(PracticeSelector.Next(Project(), new Random(1)));
    }

    [Fact]
    public void Untagged_problems_and_unlearned_patterns_are_never_offered()
    {
        // D completed out of order through its events, but A's lesson (which teaches D's extra problem's pattern) is not.
        var dFirst = Graph(D, B, C, A);
        foreach (var lesson in new[] { D, B, C })
        foreach (var s in lesson.Steps)
            Recorder.CompleteStep(lesson, s.Id);

        var state = Project(dFirst);
        Assert.Equal(new PracticeAvailability(3, 6), PracticeSelector.Availability(state));
        Assert.False(state.Pattern("pattern.a")!.IsLearned);
        Assert.Contains(state.Pattern("pattern.a")!.Steps, s => s.Step.Id == "step.d.uses-a");
        Assert.DoesNotContain(state.Patterns.SelectMany(p => p.Steps), s => s.Step.Id == "step.b.drill");
    }

    [Fact]
    public void The_choices_are_the_right_pattern_and_three_other_learned_ones()
    {
        Finish(A, B, C, D);
        var state = Project();
        for (var seed = 0; seed < 50; seed++)
        {
            var problem = PracticeSelector.Next(state, new Random(seed))!;
            Assert.Equal(problem.Step.Pattern, problem.Pattern.Id);
            Assert.Equal(4, problem.Choices.Count);
            Assert.Equal(4, problem.Choices.Select(c => c.Id).Distinct().Count());
            Assert.Contains(problem.Pattern, problem.Choices);
        }
        var firsts = Enumerable.Range(0, 50).Select(i => PracticeSelector.Choices(state, A.Patterns[0], new Random(i))[0].Id).ToHashSet();
        Assert.True(firsts.Count > 1, "choices are shuffled");
    }

    [Fact]
    public void With_three_learned_patterns_all_three_are_offered()
    {
        Finish(A, B, C);
        var problem = PracticeSelector.Next(Project(), new Random(3))!;
        Assert.Equal(["pattern.a", "pattern.b", "pattern.c"], problem.Choices.Select(c => c.Id).Order());
    }

    [Fact]
    public void The_same_seed_gives_the_same_problem()
    {
        Finish(A, B, C, D);
        var state = Project();
        var first = PracticeSelector.Next(state, new Random(42))!;
        var again = PracticeSelector.Next(state, new Random(42))!;
        Assert.Equal(first.Step.Id, again.Step.Id);
        Assert.Equal(first.Choices, again.Choices);
    }

    [Fact]
    public void Never_practised_problems_come_from_the_first_few_in_content_order()
    {
        Finish(A, B, C);
        var offered = Enumerable.Range(0, 100).Select(i => PracticeSelector.Next(Project(), new Random(i))!.Step.Id).ToHashSet();
        Assert.Equal(["step.a.p1", "step.a.p2", "step.b.p1"], offered.Order());
    }

    [Fact]
    public void A_problem_answered_wrong_comes_back_first()
    {
        Finish(A, B, C);
        Solve(C, "step.c.p2", false);
        _clock.Now = T0.AddMinutes(5);
        Assert.All(Enumerable.Range(0, 30), i => Assert.Equal("step.c.p2", PracticeSelector.Next(Project(), new Random(i))!.Step.Id));
    }

    [Fact]
    public void A_wrong_pick_needs_work_even_when_the_solve_was_right()
    {
        Finish(A, B, C);
        Pick(C, "step.c.p1", "pattern.a");
        Solve(C, "step.c.p1", true);
        var problem = Project().Pattern("pattern.c")!.Steps[0];
        Assert.True(problem.NeedsWork(T0));
        Assert.Equal("step.c.p1", PracticeSelector.Next(Project(), new Random(0))!.Step.Id);
    }

    [Fact]
    public void The_problem_just_offered_is_not_offered_again()
    {
        Finish(A, B, C);
        Solve(C, "step.c.p2", false);
        var next = PracticeSelector.Next(Project(), new Random(0), previousStepId: "step.c.p2")!;
        Assert.NotEqual("step.c.p2", next.Step.Id);
    }

    [Fact]
    public void A_lone_problem_is_offered_even_twice_in_a_row()
    {
        var a = A with { Steps = A.Steps.Take(2).ToList() };
        var b = B with { Steps = B.Steps.Take(1).ToList() };
        var c = C with { Steps = C.Steps.Take(1).ToList() };
        var graph = Graph(a, b, c);
        Finish(a, b, c);

        var state = Project(graph);
        Assert.Equal(new PracticeAvailability(3, 1), PracticeSelector.Availability(state));
        Assert.Equal("step.a.p1", PracticeSelector.Next(state, new Random(0), previousStepId: "step.a.p1")!.Step.Id);
    }

    [Fact]
    public void Among_practised_problems_the_weakest_pattern_comes_first_then_the_least_recent()
    {
        Finish(A, B, C);
        var problems = new[] { (A, "step.a.p1"), (A, "step.a.p2"), (B, "step.b.p1"), (B, "step.b.p2"), (C, "step.c.p1"), (C, "step.c.p2") };
        var minute = 0;
        foreach (var (lesson, step) in problems)
        {
            _clock.Now = T0.AddMinutes(minute++);
            Pick(lesson, step, lesson.Patterns[0].Id, run: $"run.{minute}", seconds: lesson == B ? 50 : 5);   // B's picks were slow: Hard
            Solve(lesson, step, true, run: $"run.{minute}", seconds: lesson == B ? 400 : 30);
        }

        var state = Project(now: T0.AddHours(1));
        Assert.All(state.Patterns.Where(p => p.IsLearned).SelectMany(p => p.Steps), s => Assert.False(s.NeedsWork(state.AsOf)));
        Assert.True(state.Pattern("pattern.b")!.Mastery < state.Pattern("pattern.a")!.Mastery);

        var offered = Enumerable.Range(0, 100).Select(i => PracticeSelector.Next(state, new Random(i))!.Step.Id).ToHashSet();
        Assert.Equal(["step.a.p1", "step.b.p1", "step.b.p2"], offered.Order());
    }

    // --- Practice-only problems (#127) ----------------------------------------------------------------------------

    // E teaches pattern.e with one in-lesson problem and two practice-only ones, the second using A's pattern.
    private static readonly Lesson E = Teaching("e") with
    {
        Practice = [Problem("step.e.extra", "pattern.e"), Problem("step.e.extra-a", "pattern.a")],
    };

    [Fact]
    public void Practice_problems_are_not_steps_of_the_lesson()
    {
        Finish(E);
        var lesson = Project(Graph(A, E)).Track("track.t")!.Lessons.Single(l => l.Lesson.Id == E.Id);
        Assert.Equal((LessonStatus.Completed, 3, 3), (lesson.Status, lesson.Progress.CompletedCount, lesson.Progress.TotalSteps));
    }

    [Fact]
    public void A_completed_lessons_practice_problems_join_their_patterns()
    {
        var content = Graph(A, E);
        Assert.Empty(Project(content).Pattern("pattern.e")!.Steps);   // not yet: E isn't completed

        Finish(A, E);
        var state = Project(content);
        Assert.Equal(["step.e.p1", "step.e.p2", "step.e.extra"], state.Pattern("pattern.e")!.Steps.Select(s => s.Step.Id));
        Assert.Equal(["step.a.p1", "step.a.p2", "step.e.extra-a"], state.Pattern("pattern.a")!.Steps.Select(s => s.Step.Id));
    }

    [Fact]
    public void A_practice_problem_is_offered_and_scheduled_like_a_lesson_problem()
    {
        var content = Graph(A, B, E);
        Finish(A, B, E);
        var everything = PracticeSelector.Set(Project(content), new Random(1), 100);
        Assert.Contains(everything, p => p.Step.Id == "step.e.extra" && p.Pattern.Id == "pattern.e");

        Pick(E, "step.e.extra", "pattern.e");
        Solve(E, "step.e.extra", false);

        var state = Project(content);
        var resolve = Item(state, "resolve.step.e.extra")!;
        Assert.Equal((ReviewKind.Resolve, Rating.Again, true), (resolve.Item.Kind, resolve.LastRating, resolve.IsUnlocked));
        Assert.Same(E.Practice[0], resolve.Question);
        Assert.NotNull(Item(state, "pick.step.e.extra"));
    }

    [Fact]
    public void An_untagged_practice_problem_is_a_reference_problem()
    {
        var untagged = Teaching("e") with { Practice = [Problem("step.e.extra", null)] };
        Assert.Contains("Practice problem 'step.e.extra' has no pattern, so Practice could never draw it.", Graph(untagged).ReferenceProblems());
        Assert.Empty(Graph(E, A).ReferenceProblems());
    }

    [Fact]
    public void A_practice_problem_shares_the_packs_ids()
    {
        var clash = Teaching("e") with { Practice = [Problem("step.e.p1", "pattern.e")] };
        Assert.Throws<ContentPackException>(() => Graph(clash));
        Assert.Same(E.Practice[0], Graph(A, E).Get<Step>("step.e.extra"));
    }
}
