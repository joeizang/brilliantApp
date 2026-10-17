using Brilliant.Core.Content;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

public class RatingInferenceTests
{
    private static readonly ChoiceStep Choice = new("c", "C", "?", false, [new ChoiceOption("a", true, null)]);
    private static readonly WriteCodeStep Write = new("w", "W", "?", "python", "", "f", []);

    private static Rating Infer(bool correct, int hints, double seconds, Step? step = null) =>
        RatingInference.Infer(correct, hints, TimeSpan.FromSeconds(seconds), step ?? Choice);

    [Fact]
    public void A_wrong_answer_is_Again_however_fast_and_whatever_the_hints() =>
        Assert.All(new[] { 0, 1, 3 }, hints => Assert.Equal(Rating.Again, Infer(false, hints, 1)));

    [Fact]
    public void A_quick_unaided_correct_answer_is_Easy() => Assert.Equal(Rating.Easy, Infer(true, 0, 3));

    [Fact]
    public void A_correct_answer_at_a_normal_pace_is_Good() => Assert.Equal(Rating.Good, Infer(true, 0, 15));

    [Fact]
    public void A_slow_correct_answer_is_Hard() => Assert.Equal(Rating.Hard, Infer(true, 0, 90));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Needing_a_nudge_makes_a_correct_answer_Hard_even_when_quick(int hints) =>
        Assert.Equal(Rating.Hard, Infer(true, hints, 2));

    [Fact]
    public void Being_shown_the_partial_code_or_solution_counts_as_forgotten() =>
        Assert.Equal(Rating.Again, Infer(true, 3, 2));

    [Fact]
    public void The_time_thresholds_depend_on_the_kind_of_step()
    {
        Assert.Equal(Rating.Hard, Infer(true, 0, 60, Choice));
        Assert.Equal(Rating.Easy, Infer(true, 0, 60, Write));
        Assert.Equal(Rating.Good, Infer(true, 0, 200, Write));
        Assert.Equal(Rating.Hard, Infer(true, 0, 600, Write));
    }

    [Fact]
    public void The_fast_and_slow_boundaries_are_inclusive_of_the_better_rating_only_at_the_fast_end()
    {
        var (fast, slow) = RatingInference.Thresholds(Choice);
        Assert.Equal(Rating.Easy, RatingInference.Infer(true, 0, fast, Choice));
        Assert.Equal(Rating.Good, RatingInference.Infer(true, 0, fast + TimeSpan.FromMilliseconds(1), Choice));
        Assert.Equal(Rating.Hard, RatingInference.Infer(true, 0, slow, Choice));
        Assert.Equal(Rating.Good, RatingInference.Infer(true, 0, slow - TimeSpan.FromMilliseconds(1), Choice));
    }

    [Fact]
    public void A_negative_or_zero_elapsed_time_does_not_throw() =>
        Assert.Equal(Rating.Easy, RatingInference.Infer(true, 0, TimeSpan.FromSeconds(-5), Choice));

    [Fact]
    public void A_lowered_step_is_given_longer_than_a_prediction_because_reading_generated_code_is_slower()
    {
        var lowered = new LoweredStep("l", "L", "?", "code", [], [], [new ChoiceOption("a", true, null)]);
        var predict = new PredictOutputStep("p", "P", "?", new CodeSnippet("csharp", "code"), [], [], [new ChoiceOption("a", true, null)]);

        Assert.True(RatingInference.Thresholds(lowered).Fast > RatingInference.Thresholds(predict).Fast);
        Assert.True(RatingInference.Thresholds(lowered).Slow > RatingInference.Thresholds(predict).Slow);
        Assert.Equal(LessonTestOut.Difficulty(predict), LessonTestOut.Difficulty(lowered));
        Assert.True(LessonTestOut.Difficulty(lowered) > 0);
    }

    [Fact]
    public void A_benchmark_ranking_is_given_longer_than_a_prediction_and_can_be_asked_in_a_test_out()
    {
        var bench = new BenchmarkPredictStep("b", "B", "?", "time", null, []);
        var predict = new PredictOutputStep("p", "P", "?", new CodeSnippet("csharp", "code"), [], [], [new ChoiceOption("a", true, null)]);

        Assert.True(RatingInference.Thresholds(bench).Fast > RatingInference.Thresholds(predict).Fast);
        Assert.True(RatingInference.Thresholds(bench).Slow > RatingInference.Thresholds(predict).Slow);
        Assert.True(LessonTestOut.Difficulty(bench) > 0);
    }

    [Fact]
    public void An_interleave_puzzle_is_given_longer_than_a_prediction_and_can_be_asked_in_a_test_out()
    {
        var interleave = new InterleaveStep("i", "I", "?", [], [], new(InterleaveInvariant.NoDeadlock, "d"), [], [], "e");
        var predict = new PredictOutputStep("p", "P", "?", new CodeSnippet("csharp", "code"), [], [], [new ChoiceOption("a", true, null)]);

        Assert.True(RatingInference.Thresholds(interleave).Fast > RatingInference.Thresholds(predict).Fast);
        Assert.True(RatingInference.Thresholds(interleave).Slow > RatingInference.Thresholds(predict).Slow);
        Assert.True(LessonTestOut.Difficulty(interleave) > LessonTestOut.Difficulty(predict));
    }

    [Fact]
    public void A_code_review_is_given_longer_than_a_puzzle_and_can_be_asked_in_a_test_out()
    {
        var review = new ReviewCodeStep("r", "R", "?", "csharp", "code", [], []);
        var interleave = new InterleaveStep("i", "I", "?", [], [], new(InterleaveInvariant.NoDeadlock, "d"), [], [], "e");

        Assert.True(RatingInference.Thresholds(review).Fast > RatingInference.Thresholds(interleave).Fast);
        Assert.True(RatingInference.Thresholds(review).Slow > RatingInference.Thresholds(interleave).Slow);
        Assert.True(LessonTestOut.Difficulty(review) > LessonTestOut.Difficulty(interleave));
    }

    [Fact]
    public void A_tradeoff_is_given_longer_than_a_code_review_and_can_be_asked_in_a_test_out()
    {
        var review = new ReviewCodeStep("r", "R", "?", "csharp", "code", [], []);
        var tradeoff = new DesignTradeoffStep("t", "T", "?", [], [], "m", []);

        Assert.True(RatingInference.Thresholds(tradeoff).Fast > RatingInference.Thresholds(review).Fast);
        Assert.True(RatingInference.Thresholds(tradeoff).Slow > RatingInference.Thresholds(review).Slow);
        Assert.True(LessonTestOut.Difficulty(tradeoff) > LessonTestOut.Difficulty(review));
    }
}
