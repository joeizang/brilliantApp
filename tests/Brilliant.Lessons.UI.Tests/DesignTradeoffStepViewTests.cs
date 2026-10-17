using Brilliant.Core.Content;
using Brilliant.Core.Feedback;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The design-tradeoff step (#165): pick an option, write why, then grade the justification against the revealed rubric.</summary>
public sealed class DesignTradeoffStepViewTests : ShortcutContext
{
    private static readonly DesignTradeoffStep Cache = new(
        "step.tradeoff.cache", "Where should the cache live?", "Three instances serve the same API. Where does the cache go?",
        [
            new("In-process MemoryCache", false, "Each instance caches its own copy, so they disagree."),
            new("Redis shared by all instances", true, "One copy to invalidate, at the cost of a network hop."),
        ],
        [
            new("Several instances need one consistent view", 2),
            new("Accepts the network hop", 1),
        ],
        "Use a shared cache.",
        ["A shared cache is always faster."]);

    private IRenderedComponent<DesignTradeoffStepView> Show(DesignTradeoffStep? step = null, Action<DesignTradeoffStepView.Attempt>? answered = null, Action? continued = null) =>
        Render<DesignTradeoffStepView>(p =>
        {
            p.Add(c => c.Step, step ?? Cache);
            if (answered is not null) p.Add(c => c.OnAnswered, answered);
            if (continued is not null) p.Add(c => c.OnContinue, continued);
        });

    private static void Answer(IRenderedComponent<DesignTradeoffStepView> cut, int option, string why)
    {
        cut.Find($"li[data-option='{option}'] input").Change(true);
        cut.Find("textarea.justification").Input(why);
    }

    [Fact]
    public void It_shows_the_scenario_the_options_and_a_disabled_check_button()
    {
        var cut = Show();

        Assert.Contains("Three instances", cut.Find(".prompt").TextContent);
        Assert.Equal(2, cut.FindAll("li[data-option]").Count);
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll(".model-answer"));
        Assert.Empty(cut.FindAll(".rubric"));
    }

    [Fact]
    public void Check_needs_both_an_option_and_a_justification()
    {
        var cut = Show();

        cut.Find("li[data-option='1'] input").Change(true);
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));

        cut.Find("textarea.justification").Input("   ");
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));

        cut.Find("textarea.justification").Input("Because consistency.");
        Assert.False(cut.Find("button.primary").HasAttribute("disabled"));
    }

    [Fact]
    public void Checking_grades_the_option_and_reveals_the_model_answer_and_rubric()
    {
        var cut = Show();
        Answer(cut, 1, "Because every instance sees one copy.");

        cut.Find("button.primary").Click();

        Assert.Contains("One copy to invalidate", cut.Find("li[data-option='1'] .option-feedback").TextContent);
        Assert.Contains("option right", cut.Find("li[data-option='1']").ClassName);
        Assert.Contains("Because every instance", cut.Find(".your-answer").TextContent);
        Assert.Contains("Use a shared cache.", cut.Find(".model-answer").TextContent);
        Assert.Equal(2, cut.FindAll(".rubric-point").Count);
        Assert.Single(cut.FindAll(".red-flag"));
        Assert.Empty(cut.FindAll("textarea"));
    }

    [Fact]
    public void A_wrong_option_is_marked_wrong_with_its_feedback()
    {
        var cut = Show();
        Answer(cut, 0, "Speed.");

        cut.Find("button.primary").Click();

        Assert.Contains("option wrong", cut.Find("li[data-option='0']").ClassName);
        Assert.Contains("disagree", cut.Find("li[data-option='0'] .option-feedback").TextContent);
    }

    [Fact]
    public void Nothing_is_recorded_until_the_self_assessment_is_done()
    {
        DesignTradeoffStepView.Attempt? attempt = null;
        var cut = Show(answered: a => attempt = a);
        Answer(cut, 1, "Consistency.");

        cut.Find("button.primary").Click();

        Assert.Null(attempt);
    }

    [Fact]
    public void Ticking_the_rubric_sets_the_grade_that_is_recorded()
    {
        DesignTradeoffStepView.Attempt? attempt = null;
        var cut = Show(answered: a => attempt = a);
        Answer(cut, 1, "Consistency across instances.");
        cut.Find("button.primary").Click();

        cut.Find(".rubric-point[data-point='0'] input").Change(true);
        cut.Find("button.primary").Click();

        Assert.NotNull(attempt);
        Assert.True(attempt.Correct);
        Assert.Equal(1, attempt.Option);
        Assert.Equal("Consistency across instances.", attempt.Justification);
        Assert.Equal([0], attempt.PointsMade);
        Assert.Empty(attempt.RedFlags);
        Assert.Equal(0.4 + 0.6 * (2.0 / 3), attempt.Score, 10);
        Assert.Contains("right option", cut.Find(".verdict").TextContent);
    }

    [Fact]
    public void Ticking_nothing_is_a_poor_grade_even_with_the_right_option()
    {
        DesignTradeoffStepView.Attempt? attempt = null;
        var cut = Show(answered: a => attempt = a);
        Answer(cut, 1, "Dunno.");
        cut.Find("button.primary").Click();

        cut.Find("button.primary").Click();

        Assert.False(attempt!.Correct);
        Assert.Contains("incorrect", cut.Find(".verdict").ClassName);
    }

    [Fact]
    public void A_ticked_red_flag_makes_it_incorrect_and_is_recorded()
    {
        DesignTradeoffStepView.Attempt? attempt = null;
        var cut = Show(answered: a => attempt = a);
        Answer(cut, 1, "Redis is faster.");
        cut.Find("button.primary").Click();
        cut.Find(".rubric-point[data-point='0'] input").Change(true);
        cut.Find(".red-flag[data-flag='0'] input").Change(true);

        cut.Find("button.primary").Click();

        Assert.False(attempt!.Correct);
        Assert.Equal([0], attempt.RedFlags);
    }

    [Fact]
    public void A_tick_can_be_taken_back()
    {
        DesignTradeoffStepView.Attempt? attempt = null;
        var cut = Show(answered: a => attempt = a);
        Answer(cut, 1, "Consistency.");
        cut.Find("button.primary").Click();

        cut.Find(".rubric-point[data-point='0'] input").Change(true);
        cut.Find(".rubric-point[data-point='0'] input").Change(false);
        cut.Find("button.primary").Click();

        Assert.Empty(attempt!.PointsMade);
    }

    [Fact]
    public void A_step_without_red_flags_shows_no_red_flag_section()
    {
        var cut = Show(Cache with { RedFlags = [] });
        Answer(cut, 1, "Consistency.");

        cut.Find("button.primary").Click();

        Assert.Empty(cut.FindAll(".red-flags"));
    }

    [Fact]
    public void The_answer_is_reported_once_and_continue_moves_on()
    {
        var answers = 0;
        var continued = 0;
        var cut = Show(answered: _ => answers++, continued: () => continued++);
        Answer(cut, 1, "Consistency.");
        cut.Find("button.primary").Click();
        cut.Find("button.primary").Click();

        cut.Find("button.primary").Click();

        Assert.Equal(1, answers);
        Assert.Equal(1, continued);
    }

    [Fact]
    public void A_different_step_starts_afresh()
    {
        var cut = Show();
        Answer(cut, 1, "Consistency.");
        cut.Find("button.primary").Click();

        cut.Render(p => p.Add(c => c.Step, Cache with { Id = "step.tradeoff.other" }));

        Assert.Single(cut.FindAll("textarea.justification"));
        Assert.Empty(cut.FindAll(".model-answer"));
    }

    [Fact]
    public async Task A_number_key_picks_an_option_before_checking()
    {
        var cut = Show();

        await Press(StepCommand.Choose, 2);

        Assert.True(cut.Find("li[data-option='1'] input").HasAttribute("checked"));
    }

    [Fact]
    public async Task Return_moves_on_from_the_self_assessment_and_then_the_result()
    {
        DesignTradeoffStepView.Attempt? attempt = null;
        var continued = 0;
        var cut = Show(answered: a => attempt = a, continued: () => continued++);
        Answer(cut, 1, "Consistency.");
        cut.Find("button.primary").Click();

        await Press(StepCommand.Advance);
        Assert.NotNull(attempt);
        await Press(StepCommand.Advance);

        Assert.Equal(1, continued);
    }

    // ---- Advisory feedback from a local model (#166) ----

    private sealed class FakeGrader : IAnswerGrader
    {
        public List<GradeRequest> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public TaskCompletionSource<AnswerFeedback> Reply { get; } = new();
        public bool Enabled { get; set; } = true;

        public Task<AnswerFeedback> GradeAsync(GradeRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            Tokens.Add(ct);
            return Reply.Task;
        }
    }

    private static readonly AnswerFeedback Verdict = new(
        [new(0, "Several instances need one consistent view", true, "You said every instance sees one copy."), new(1, "Accepts the network hop", false, "You never mentioned the hop.")],
        [new(0, "A shared cache is always faster.", "You called it faster.")]);

    private FakeGrader UseGrader()
    {
        var grader = new FakeGrader();
        Services.AddSingleton<IAnswerGrader>(grader);
        return grader;
    }

    private static void SelfAssess(IRenderedComponent<DesignTradeoffStepView> cut, params int[] points)
    {
        Answer(cut, 1, "Because every instance sees one copy.");
        cut.Find("button.primary").Click();
        foreach (var point in points) cut.Find($".rubric-point[data-point='{point}'] input").Change(true);
    }

    [Fact]
    public void Without_a_grader_there_is_no_feedback_section()
    {
        var cut = Show();
        SelfAssess(cut, 0);

        cut.Find("button.primary").Click();

        Assert.Empty(cut.FindAll(".feedback"));
    }

    [Fact]
    public void A_grader_that_is_switched_off_is_treated_as_absent()
    {
        var grader = UseGrader();
        grader.Enabled = false;
        var cut = Show();
        SelfAssess(cut, 0);

        cut.Find("button.primary").Click();

        Assert.Empty(grader.Requests);
        Assert.Empty(cut.FindAll(".feedback"));
    }

    [Fact]
    public void Feedback_is_not_asked_for_until_the_self_assessment_is_done()
    {
        var grader = UseGrader();
        var cut = Show();

        Answer(cut, 1, "Because every instance sees one copy.");
        cut.Find("button.primary").Click();
        cut.Find(".rubric-point[data-point='0'] input").Change(true);

        Assert.Empty(grader.Requests);
        Assert.Empty(cut.FindAll(".feedback"));
    }

    [Fact]
    public void Finishing_the_self_assessment_asks_the_grader_with_the_option_and_the_answer_as_written()
    {
        var grader = UseGrader();
        var cut = Show();
        SelfAssess(cut, 0);

        cut.Find("button.primary").Click();

        var request = Assert.Single(grader.Requests);
        Assert.Equal("Redis shared by all instances", request.Option);
        Assert.Equal("Because every instance sees one copy.", request.Answer);
        Assert.Equal(2, request.RubricPoints.Count);
    }

    [Fact]
    public void While_the_model_thinks_the_result_and_Continue_are_already_there()
    {
        UseGrader();
        var continued = 0;
        var cut = Show(continued: () => continued++);
        SelfAssess(cut, 0);

        cut.Find("button.primary").Click();

        Assert.Contains("Getting feedback", cut.Find(".feedback-pending").TextContent);
        Assert.NotEmpty(cut.FindAll(".verdict"));
        cut.Find("button.primary").Click();
        Assert.Equal(1, continued);
    }

    [Fact]
    public void Feedback_shows_a_verdict_and_a_note_for_every_rubric_point_and_the_red_flags_found()
    {
        var grader = UseGrader();
        var cut = Show();
        SelfAssess(cut, 0);
        cut.Find("button.primary").Click();

        grader.Reply.SetResult(Verdict);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".feedback-point").Count));
        var hit = cut.Find(".feedback-point[data-point='0']");
        Assert.Equal("hit", hit.GetAttribute("data-verdict"));
        Assert.Contains("every instance sees one copy", hit.TextContent);
        Assert.Contains("Several instances need one consistent view", hit.TextContent);
        var miss = cut.Find(".feedback-point[data-point='1']");
        Assert.Equal("missed", miss.GetAttribute("data-verdict"));
        Assert.Contains("never mentioned the hop", miss.TextContent);
        Assert.Contains("You called it faster.", cut.Find(".feedback-flag[data-flag='0']").TextContent);
        Assert.Empty(cut.FindAll(".feedback-pending"));
    }

    [Fact]
    public void A_clean_answer_with_no_red_flags_found_says_nothing_about_them()
    {
        var grader = UseGrader();
        var cut = Show();
        SelfAssess(cut, 0);
        cut.Find("button.primary").Click();

        grader.Reply.SetResult(Verdict with { RedFlagsFound = [] });

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".feedback-point")));
        Assert.Empty(cut.FindAll(".feedback-flag"));
    }

    [Fact]
    public void Feedback_says_it_is_advisory_and_changes_neither_the_grade_nor_the_recorded_attempt()
    {
        var grader = UseGrader();
        DesignTradeoffStepView.Attempt? attempt = null;
        var answers = 0;
        var cut = Show(answered: a => { attempt = a; answers++; });
        SelfAssess(cut);   // the learner ticks nothing
        cut.Find("button.primary").Click();
        var scoreBefore = cut.Find(".score").TextContent;

        // The model thinks every point was made; the learner's own ticks still set the grade.
        grader.Reply.SetResult(new AnswerFeedback([new(0, "p", true, "n"), new(1, "q", true, "n")], []));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".feedback-point")));
        Assert.Contains("advisory", cut.Find(".feedback").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.False(attempt!.Correct);
        Assert.Empty(attempt.PointsMade);
        Assert.Equal(1, answers);
        Assert.Equal(scoreBefore, cut.Find(".score").TextContent);
        Assert.Contains("incorrect", cut.Find(".verdict").ClassName);
    }

    [Fact]
    public void An_unavailable_model_says_why_and_the_step_still_finishes()
    {
        var grader = UseGrader();
        var continued = 0;
        var cut = Show(continued: () => continued++);
        SelfAssess(cut, 0);
        cut.Find("button.primary").Click();

        grader.Reply.SetException(new AnswerGraderException(AnswerGraderFailure.Unavailable, "Ollama isn't running. Start it on this Mac to get feedback."));

        cut.WaitForAssertion(() => Assert.Contains("Ollama isn't running", cut.Find(".feedback-unavailable").TextContent));
        Assert.Empty(cut.FindAll(".feedback-point"));
        Assert.Empty(cut.FindAll(".feedback-pending"));
        cut.Find("button.primary").Click();
        Assert.Equal(1, continued);
    }

    [Fact]
    public void A_failure_nobody_planned_for_is_also_just_unavailable_feedback()
    {
        var grader = UseGrader();
        var cut = Show();
        SelfAssess(cut, 0);
        cut.Find("button.primary").Click();

        grader.Reply.SetException(new InvalidOperationException("boom: internal detail"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".feedback-unavailable")));
        Assert.DoesNotContain("boom", cut.Markup);
        Assert.Contains("unavailable", cut.Find(".feedback-unavailable").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Moving_to_another_step_cancels_the_request_and_drops_a_late_answer()
    {
        var grader = UseGrader();
        var cut = Show();
        SelfAssess(cut, 0);
        cut.Find("button.primary").Click();

        cut.Render(p => p.Add(c => c.Step, Cache with { Id = "step.tradeoff.other" }));
        grader.Reply.SetResult(Verdict);

        Assert.True(grader.Tokens[0].IsCancellationRequested);
        Assert.Empty(cut.FindAll(".feedback"));
        Assert.Empty(cut.FindAll(".feedback-point"));
    }

    [Fact]
    public async Task Leaving_the_screen_cancels_the_request()
    {
        var grader = UseGrader();
        var cut = Show();
        SelfAssess(cut, 0);
        cut.Find("button.primary").Click();

        await DisposeComponentsAsync();

        Assert.True(grader.Tokens[0].IsCancellationRequested);
    }
}
