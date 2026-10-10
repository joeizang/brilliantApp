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
}
