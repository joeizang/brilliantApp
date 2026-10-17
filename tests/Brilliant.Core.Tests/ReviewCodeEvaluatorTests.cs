using System.Text.Json;
using Brilliant.Core.Content;

namespace Brilliant.Core.Tests;

/// <summary>The review-code evaluator: flagged lines graded against authored defects, with partial credit and per-defect feedback (#164).</summary>
public sealed class ReviewCodeEvaluatorTests
{
    // Line 1  public sealed class Cache
    // Line 2  {
    // Line 3      private readonly Dictionary<string, string> _items = new();
    // Line 4      public string Get(string key)
    // Line 5      {
    // Line 6          var found = _items.ContainsKey(key);
    // Line 7          if (found = true) return _items[key];
    // Line 8          return Load(key);
    // Line 9      }
    // Line 10 }
    private static ReviewCodeStep Sample() => new(
        "step.review.cache", "Review the cache", "Review this change.", "csharp",
        string.Join('\n',
            "public sealed class Cache",
            "{",
            "    private readonly Dictionary<string, string> _items = new();",
            "    public string Get(string key)",
            "    {",
            "        var found = _items.ContainsKey(key);",
            "        if (found = true) return _items[key];",
            "        return Load(key);",
            "    }",
            "}"),
        [
            new(3, 3, ReviewCategory.Concurrency, "A plain Dictionary is not safe for concurrent readers and writers."),
            new(7, 7, ReviewCategory.Bug, "`=` assigns; this is always true and throws on a miss."),
        ],
        [new(8, "Calling Load on a miss is the point of a cache; this line is fine.")]);

    [Fact]
    public void Flagging_every_defect_with_its_category_is_a_perfect_review()
    {
        var result = ReviewCodeEvaluator.Evaluate(Sample(), [new(3, ReviewCategory.Concurrency), new(7, ReviewCategory.Bug)]);

        Assert.True(result.IsCorrect);
        Assert.Equal(1.0, result.Score);
        Assert.All(result.Defects, d => Assert.Equal(ReviewDefectOutcome.Found, d.Outcome));
        Assert.Empty(result.FalseFlags);
    }

    [Fact]
    public void Flagging_the_right_line_with_the_wrong_category_earns_half_credit()
    {
        var result = ReviewCodeEvaluator.Evaluate(Sample(), [new(3, ReviewCategory.Security), new(7, ReviewCategory.Bug)]);

        Assert.False(result.IsCorrect);
        var cache = result.Defects[0];
        Assert.Equal(ReviewDefectOutcome.WrongCategory, cache.Outcome);
        Assert.Equal(0.5, cache.Credit);
        Assert.Equal(0.75, result.Score);
        Assert.Contains("concurrency", cache.Feedback);
    }

    [Fact]
    public void A_defect_that_is_not_flagged_is_missed_and_still_explained()
    {
        var result = ReviewCodeEvaluator.Evaluate(Sample(), [new(7, ReviewCategory.Bug)]);

        var missed = result.Defects[0];
        Assert.Equal(ReviewDefectOutcome.Missed, missed.Outcome);
        Assert.Equal(0, missed.Credit);
        Assert.Contains("not safe for concurrent", missed.Feedback);
        Assert.Equal(0.5, result.Score);
        Assert.False(result.IsCorrect);
    }

    [Fact]
    public void A_flag_on_a_line_with_no_defect_is_a_false_flag_that_costs_a_quarter_of_a_defect()
    {
        var result = ReviewCodeEvaluator.Evaluate(Sample(), [new(3, ReviewCategory.Concurrency), new(7, ReviewCategory.Bug), new(6, ReviewCategory.Performance)]);

        var flag = Assert.Single(result.FalseFlags);
        Assert.Equal(6, flag.Line);
        Assert.Equal(0.875, result.Score);
        Assert.False(result.IsCorrect);
    }

    [Fact]
    public void A_false_flag_on_an_authored_decoy_explains_why_the_line_is_fine()
    {
        var result = ReviewCodeEvaluator.Evaluate(Sample(), [new(8, ReviewCategory.Bug)]);

        var flag = Assert.Single(result.FalseFlags);
        Assert.Contains("this line is fine", flag.Feedback);
    }

    [Fact]
    public void A_false_flag_without_a_decoy_note_gets_generic_feedback()
    {
        var result = ReviewCodeEvaluator.Evaluate(Sample(), [new(2, ReviewCategory.Api)]);

        Assert.Contains("nothing wrong", Assert.Single(result.FalseFlags).Feedback);
    }

    [Fact]
    public void Flagging_everything_cannot_score_below_zero()
    {
        var flags = Enumerable.Range(1, 10).Select(l => new ReviewFlag(l, ReviewCategory.Bug)).ToList();

        var result = ReviewCodeEvaluator.Evaluate(Sample(), flags);

        Assert.InRange(result.Score, 0, 1);
        Assert.Equal(8, result.FalseFlags.Count);
    }

    [Fact]
    public void An_empty_review_misses_everything_and_scores_nothing()
    {
        var result = ReviewCodeEvaluator.Evaluate(Sample(), []);

        Assert.Equal(0, result.Score);
        Assert.All(result.Defects, d => Assert.Equal(ReviewDefectOutcome.Missed, d.Outcome));
        Assert.Empty(result.FalseFlags);
    }

    [Fact]
    public void Any_line_of_a_multi_line_defect_finds_it()
    {
        var step = Sample() with { Defects = [new(6, 7, ReviewCategory.Bug, "The check and the use disagree.")], Decoys = [] };

        var result = ReviewCodeEvaluator.Evaluate(step, [new(7, ReviewCategory.Bug)]);

        Assert.True(result.IsCorrect);
        Assert.Empty(result.FalseFlags);
    }

    [Fact]
    public void Two_flags_in_one_defect_with_one_right_category_still_find_it_and_are_not_false_flags()
    {
        var step = Sample() with { Defects = [new(6, 7, ReviewCategory.Bug, "The check and the use disagree.")], Decoys = [] };

        var result = ReviewCodeEvaluator.Evaluate(step, [new(6, ReviewCategory.Performance), new(7, ReviewCategory.Bug)]);

        Assert.Equal(ReviewDefectOutcome.Found, result.Defects[0].Outcome);
        Assert.Empty(result.FalseFlags);
        Assert.True(result.IsCorrect);
    }

    [Fact]
    public void Flagging_the_same_line_twice_counts_once()
    {
        var result = ReviewCodeEvaluator.Evaluate(Sample(), [new(2, ReviewCategory.Api), new(2, ReviewCategory.Api), new(3, ReviewCategory.Concurrency), new(7, ReviewCategory.Bug)]);

        Assert.Single(result.FalseFlags);
    }

    [Fact]
    public void Flags_outside_the_code_or_with_an_unknown_category_are_ignored()
    {
        var result = ReviewCodeEvaluator.Evaluate(Sample(), [new(99, ReviewCategory.Bug), new(0, ReviewCategory.Bug), new(3, "style"), new(7, ReviewCategory.Bug)]);

        Assert.Empty(result.FalseFlags);
        Assert.Equal(ReviewDefectOutcome.Missed, result.Defects[0].Outcome);
    }

    [Fact]
    public void The_defects_are_reported_in_code_order_whatever_order_they_were_authored_in()
    {
        var step = Sample() with { Defects = [Sample().Defects[1], Sample().Defects[0]] };

        var result = ReviewCodeEvaluator.Evaluate(step, []);

        Assert.Equal([3, 7], result.Defects.Select(d => d.Defect.Line));
    }

    [Fact]
    public void The_summary_says_how_many_defects_were_found()
    {
        var result = ReviewCodeEvaluator.Evaluate(Sample(), [new(3, ReviewCategory.Concurrency)]);

        Assert.Contains("1 of 2", result.Summary);
    }

    [Fact]
    public void Lines_are_numbered_from_one_and_handle_windows_line_endings()
    {
        var step = Sample() with { Code = "a\r\nb\r\nc" };

        Assert.Equal(["a", "b", "c"], step.Lines);
    }

    [Fact]
    public void A_review_code_step_round_trips_through_the_pack_json()
    {
        Step original = Sample();

        var json = JsonSerializer.Serialize(original);
        var back = JsonSerializer.Deserialize<Step>(json);

        var review = Assert.IsType<ReviewCodeStep>(back);
        Assert.Equal(2, review.Defects.Count);
        Assert.Equal(ReviewCategory.Concurrency, review.Defects[0].Category);
        Assert.Single(review.Decoys);
    }
}
