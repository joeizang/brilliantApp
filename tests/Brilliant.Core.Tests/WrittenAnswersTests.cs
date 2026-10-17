using Brilliant.Core.Content;
using Brilliant.Core.Feedback;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>The written design-tradeoff answers in the (synced) log, so feedback can be asked for later on a machine that has a model (#167).</summary>
public sealed class WrittenAnswersTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static readonly DesignTradeoffStep Tradeoff = GradingPromptTests.Cache;
    private static readonly ExplainStep Explain = new("step.explain", "Intro", "b", [], null);
    private static readonly Lesson L1 = new("lesson.1", "One", "track.a", [Explain, Tradeoff]);
    private static readonly Lesson L2 = new("lesson.2", "Two", "track.a", [Tradeoff with { Id = "step.tradeoff.second", Title = "Second" }]);
    private static readonly ContentGraph Content = new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.a", "A", [L1.Id, L2.Id])], [L1, L2]);

    // What the app saves with an answer: the fingerprint of the step as it was then.
    private static string Ctx(string stepId, int option) =>
        option < 0 || option >= Tradeoff.Options.Count ? "n/a" : GradeRequest.ContextOf(stepId == "step.tradeoff.second" ? L2.AllSteps().OfType<DesignTradeoffStep>().Single() : Tradeoff, option);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder(string device = "phone") => new(_log, device, _clock);
    private IReadOnlyList<WrittenAnswer> Answers => WrittenAnswers.From(_log.ReadAll(), Content);

    [Fact]
    public void A_lesson_answer_is_listed_with_the_step_the_option_and_the_words_as_written()
    {
        Recorder().TradeoffSubmitted("lesson.1", Tradeoff.Id, true, 0.8, 1, "Because every instance sees one copy.", [0], [], Ctx(Tradeoff.Id, 1));

        var answer = Assert.Single(Answers);
        Assert.Equal(("lesson.1", Tradeoff.Id), (answer.LessonId, answer.Step.Id));
        Assert.Same(Tradeoff, answer.Step);
        Assert.Equal("One", answer.LessonTitle);
        Assert.Equal(1, answer.Option);
        Assert.Equal("Because every instance sees one copy.", answer.Justification);
        Assert.Equal("phone", answer.DeviceId);
        Assert.Equal(_clock.Now, answer.At);
    }

    [Fact]
    public void A_review_answer_is_listed_too()
    {
        var item = new ReviewItem("review.cache", "concept.cache", Tradeoff.Id);
        Recorder().ReviewAnswered("lesson.1", item, true, 0, TimeSpan.FromSeconds(30), Rating.Good, ProgressRecorder.TradeoffDetail(0.8, 0, "A cache per instance.", [], [1], GradeRequest.ContextOf(Tradeoff, 0)));

        var answer = Assert.Single(Answers);
        Assert.Equal((0, "A cache per instance."), (answer.Option, answer.Justification));
    }

    [Fact]
    public void A_review_answer_that_carries_no_written_detail_is_not_listed()
    {
        var item = new ReviewItem("review.cache", "concept.cache", Tradeoff.Id);
        Recorder().ReviewAnswered("lesson.1", item, true, 0, TimeSpan.FromSeconds(30), Rating.Good);

        Assert.Empty(Answers);
    }

    [Fact]
    public void Answers_to_other_kinds_of_step_are_not_listed()
    {
        Recorder().StepAnswered("lesson.1", Explain.Id, true, "x");

        Assert.Empty(Answers);
    }

    [Fact]
    public void An_answer_on_a_step_content_no_longer_has_is_not_listed()
    {
        Recorder().TradeoffSubmitted("lesson.1", "step.gone", true, 1, 0, "Words.", [], [], Ctx("step.gone", 0));
        Recorder().TradeoffSubmitted("lesson.gone", Tradeoff.Id, true, 1, 0, "Words.", [], [], Ctx(Tradeoff.Id, 0));

        Assert.Empty(Answers);
    }

    [Fact]
    public void An_option_outside_the_step_or_a_blank_justification_is_skipped_not_fatal()
    {
        Recorder().TradeoffSubmitted("lesson.1", Tradeoff.Id, true, 1, 7, "Words.", [], [], Ctx(Tradeoff.Id, 7));
        Recorder().TradeoffSubmitted("lesson.1", Tradeoff.Id, true, 1, 0, "   ", [], [], Ctx(Tradeoff.Id, 0));
        _log.Append(new ProgressEvent("bad-1", "phone", _clock.Now, ProgressEventTypes.StepAnswered, "lesson.1", Tradeoff.Id, "not json"));
        _log.Append(new ProgressEvent("bad-2", "phone", _clock.Now, ProgressEventTypes.StepAnswered, "lesson.1", Tradeoff.Id, null));
        Recorder().TradeoffSubmitted("lesson.1", Tradeoff.Id, true, 1, 1, "Fine.", [], [], Ctx(Tradeoff.Id, 1));

        Assert.Equal("Fine.", Assert.Single(Answers).Justification);
    }

    [Fact]
    public void Newest_first_whichever_device_gave_the_answer()
    {
        Recorder("phone").TradeoffSubmitted("lesson.1", Tradeoff.Id, true, 1, 1, "Older, on the phone.", [], [], Ctx(Tradeoff.Id, 1));
        _clock.Now += TimeSpan.FromHours(1);
        Recorder("mac").TradeoffSubmitted("lesson.2", "step.tradeoff.second", true, 1, 1, "Newer, on the Mac.", [], [], Ctx("step.tradeoff.second", 1));

        Assert.Equal(["Newer, on the Mac.", "Older, on the phone."], Answers.Select(a => a.Justification));
        Assert.Equal(["mac", "phone"], Answers.Select(a => a.DeviceId));
    }

    [Fact]
    public void An_event_that_arrives_twice_is_listed_once()
    {
        Recorder().TradeoffSubmitted("lesson.1", Tradeoff.Id, true, 1, 1, "Once.", [], [], Ctx(Tradeoff.Id, 1));
        _log.Items.Add(_log.Items[0]);

        Assert.Single(Answers);
    }

    [Fact]
    public void Only_the_newest_answers_are_listed_up_to_the_limit()
    {
        for (var i = 0; i < WrittenAnswers.MaxListed + 5; i++)
        {
            _clock.Now += TimeSpan.FromMinutes(1);
            Recorder().TradeoffSubmitted("lesson.1", Tradeoff.Id, true, 1, 1, $"Answer {i}", [], [], Ctx(Tradeoff.Id, 1));
        }

        var answers = Answers;
        Assert.Equal(WrittenAnswers.MaxListed, answers.Count);
        Assert.Equal($"Answer {WrittenAnswers.MaxListed + 4}", answers[0].Justification);
    }

    [Fact]
    public void The_grade_request_is_built_from_the_step_and_what_was_written()
    {
        Recorder().TradeoffSubmitted("lesson.1", Tradeoff.Id, true, 1, 1, "Because every instance sees one copy.", [], [], Ctx(Tradeoff.Id, 1));

        var request = Assert.Single(Answers).ToGradeRequest();

        Assert.Equal(GradingPromptTests.Request(), request, new GradeRequestComparer());
    }

    private sealed class GradeRequestComparer : IEqualityComparer<GradeRequest>
    {
        public bool Equals(GradeRequest? a, GradeRequest? b) =>
            a is not null && b is not null && (a.Scenario, a.Option, a.Answer, a.ModelAnswer) == (b.Scenario, b.Option, b.Answer, b.ModelAnswer)
            && a.RubricPoints.SequenceEqual(b.RubricPoints) && a.RedFlags.SequenceEqual(b.RedFlags);
        public int GetHashCode(GradeRequest r) => r.Scenario.GetHashCode();
    }

    // ---- An answer belongs to the step as it was when answered ----

    private static ContentGraph WithStep(DesignTradeoffStep revised)
    {
        var lesson = L1 with { Steps = [Explain, revised] };
        return new ContentGraph(new PackManifest("pack.t", "1.1.0", 1), [new Track("track.a", "A", [lesson.Id, L2.Id])], [lesson, L2]);
    }

    [Fact]
    public void A_content_update_that_reorders_the_options_does_not_turn_an_answer_into_another_choice()
    {
        Recorder().TradeoffSubmitted("lesson.1", Tradeoff.Id, true, 1, 1, "Redis, so every instance sees one copy.", [0], [], Ctx(Tradeoff.Id, 1));
        var swapped = Tradeoff with { Options = [Tradeoff.Options[1], Tradeoff.Options[0]] };

        var scan = WrittenAnswers.Scan(_log.ReadAll(), WithStep(swapped));

        Assert.Empty(scan.Answers);
        Assert.Equal(1, scan.Changed);
    }

    [Fact]
    public void A_reworded_scenario_rubric_model_answer_or_red_flag_also_retires_the_answer()
    {
        Recorder().TradeoffSubmitted("lesson.1", Tradeoff.Id, true, 1, 1, "Words.", [0], [], Ctx(Tradeoff.Id, 1));

        foreach (var revised in new[]
        {
            Tradeoff with { Prompt = "A different scenario." },
            Tradeoff with { ModelAnswer = "A different model answer." },
            Tradeoff with { Rubric = [new("A different point", 1)] },
            Tradeoff with { RedFlags = ["A different red flag."] },
        })
            Assert.Equal(1, WrittenAnswers.Scan(_log.ReadAll(), WithStep(revised)).Changed);
    }

    [Fact]
    public void Changes_that_do_not_touch_what_is_graded_leave_the_answer_listed()
    {
        Recorder().TradeoffSubmitted("lesson.1", Tradeoff.Id, true, 1, 1, "Words.", [0], [], Ctx(Tradeoff.Id, 1));
        var retitled = Tradeoff with { Title = "A new title" };

        var scan = WrittenAnswers.Scan(_log.ReadAll(), WithStep(retitled));

        Assert.Single(scan.Answers);
        Assert.Equal(0, scan.Changed);
    }

    [Fact]
    public void An_answer_saved_before_there_was_a_fingerprint_is_counted_but_not_offered()
    {
        _log.Append(new ProgressEvent("old", "phone", _clock.Now, ProgressEventTypes.StepAnswered, "lesson.1", Tradeoff.Id,
            """{"correct":true,"score":1,"option":1,"justification":"Words.","pointsMade":[],"redFlags":[]}"""));

        var scan = WrittenAnswers.Scan(_log.ReadAll(), Content);

        Assert.Empty(scan.Answers);
        Assert.Equal(1, scan.Changed);
    }

    [Fact]
    public void The_fingerprint_ignores_the_learners_words_and_tells_options_apart()
    {
        Assert.Equal(GradeRequest.For(Tradeoff, 1, "one").Fingerprint(), GradeRequest.For(Tradeoff, 1, "two").Fingerprint());
        Assert.NotEqual(GradeRequest.ContextOf(Tradeoff, 0), GradeRequest.ContextOf(Tradeoff, 1));
    }
}
