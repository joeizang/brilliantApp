using System.Text.Json;
using Brilliant.Core.Content;

namespace Brilliant.Core.Tests;

/// <summary>The design-tradeoff evaluator: the option is graded automatically, the written justification by the learner's own ticks against the rubric (#165).</summary>
public sealed class DesignTradeoffEvaluatorTests
{
    private static DesignTradeoffStep Sample() => new(
        "step.tradeoff.cache", "Where should the cache live?", "Pick one.",
        [
            new("In-process MemoryCache", false, "Fast, but each instance caches its own copy, so they disagree."),
            new("Redis shared by all instances", true, "One copy to invalidate, at the cost of a network hop."),
            new("No cache", false, "Correct but ignores the load."),
        ],
        [
            new("Names that several instances need one consistent view", 2),
            new("Accepts the network hop as the price", 1),
            new("Mentions how entries expire or are invalidated", 1),
        ],
        "Use a shared cache: consistency across instances matters more than the saved hop, and expiry keeps it honest.",
        ["A shared cache is always faster than in-process memory."]);

    [Fact]
    public void The_right_option_with_every_point_made_is_a_perfect_answer()
    {
        var result = DesignTradeoffEvaluator.Evaluate(Sample(), 1, [0, 1, 2], []);

        Assert.True(result.OptionCorrect);
        Assert.True(result.IsCorrect);
        Assert.Equal(1.0, result.Score);
        Assert.Equal(1.0, result.Coverage);
    }

    [Fact]
    public void Points_are_weighted()
    {
        var result = DesignTradeoffEvaluator.Evaluate(Sample(), 1, [0], []);

        Assert.Equal(0.5, result.Coverage);
        Assert.Equal(0.4 + 0.6 * 0.5, result.Score, 10);
    }

    [Fact]
    public void Enough_of_the_rubric_with_the_right_option_is_correct()
    {
        Assert.True(DesignTradeoffEvaluator.Evaluate(Sample(), 1, [0, 1], []).IsCorrect);
        Assert.False(DesignTradeoffEvaluator.Evaluate(Sample(), 1, [1, 2], []).IsCorrect);
    }

    [Fact]
    public void The_wrong_option_is_never_correct_however_good_the_justification()
    {
        var result = DesignTradeoffEvaluator.Evaluate(Sample(), 0, [0, 1, 2], []);

        Assert.False(result.OptionCorrect);
        Assert.False(result.IsCorrect);
        Assert.Equal(0.6, result.Score, 10);
    }

    [Fact]
    public void A_claimed_red_flag_makes_the_answer_incorrect_and_costs_a_tenth()
    {
        var result = DesignTradeoffEvaluator.Evaluate(Sample(), 1, [0, 1, 2], [0]);

        Assert.False(result.IsCorrect);
        Assert.Equal(0.9, result.Score, 10);
        Assert.Equal(1, result.RedFlagsClaimed);
    }

    [Fact]
    public void Nothing_ticked_and_no_option_scores_zero()
    {
        var result = DesignTradeoffEvaluator.Evaluate(Sample(), null, [], []);

        Assert.Equal(0, result.Score);
        Assert.False(result.IsCorrect);
    }

    [Fact]
    public void Out_of_range_and_repeated_ticks_are_ignored()
    {
        var result = DesignTradeoffEvaluator.Evaluate(Sample(), 9, [0, 0, 7, -1], [5]);

        Assert.False(result.OptionCorrect);
        Assert.Equal(0.4 * 0 + 0.6 * 0.5, result.Score, 10);
        Assert.Equal(0, result.RedFlagsClaimed);
    }

    [Fact]
    public void The_score_never_goes_below_zero()
    {
        var step = Sample() with { RedFlags = ["a", "b", "c", "d"] };

        Assert.Equal(0, DesignTradeoffEvaluator.Evaluate(step, 0, [], [0, 1, 2, 3]).Score);
    }

    [Fact]
    public void The_chosen_options_feedback_is_returned()
    {
        Assert.Contains("disagree", DesignTradeoffEvaluator.Evaluate(Sample(), 0, [], []).OptionFeedback);
        Assert.Contains("One copy", DesignTradeoffEvaluator.Evaluate(Sample(), 1, [], []).OptionFeedback);
    }

    [Fact]
    public void The_summary_says_how_the_justification_went()
    {
        var summary = DesignTradeoffEvaluator.Evaluate(Sample(), 1, [0, 1], [0]).Summary;

        Assert.Contains("right option", summary);
        Assert.Contains("2 of 3", summary);
        Assert.Contains("1 red flag", summary);
    }

    [Fact]
    public void A_step_with_no_red_flags_is_gradable()
    {
        var step = Sample() with { RedFlags = [] };

        Assert.True(DesignTradeoffEvaluator.Evaluate(step, 1, [0, 1, 2], []).IsCorrect);
    }

    [Fact]
    public void A_design_tradeoff_step_round_trips_through_the_pack_json()
    {
        Step original = Sample();

        var back = Assert.IsType<DesignTradeoffStep>(JsonSerializer.Deserialize<Step>(JsonSerializer.Serialize(original)));

        Assert.Equal(3, back.Options.Count);
        Assert.Equal(2, back.Rubric[0].Points);
        Assert.Single(back.RedFlags);
        Assert.StartsWith("Use a shared cache", back.ModelAnswer);
    }
}
