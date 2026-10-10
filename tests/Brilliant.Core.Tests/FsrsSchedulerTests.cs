using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>
/// FSRS-5 with the published default parameters. The expected numbers were worked out independently from the
/// published formulas (see docs/review.md), not copied from the implementation.
/// </summary>
public class FsrsSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(Rating.Again, 0.40255, 7.1949, 1)]
    [InlineData(Rating.Hard, 1.18385, 6.4883, 1)]
    [InlineData(Rating.Good, 3.173, 5.2824, 3)]
    [InlineData(Rating.Easy, 15.69105, 3.2245, 16)]
    public void A_first_review_starts_from_the_default_stability_and_difficulty(Rating rating, double stability, double difficulty, int days)
    {
        var card = FsrsScheduler.Schedule(null, rating, T0);

        Assert.Equal(stability, card.Stability, 4);
        Assert.Equal(difficulty, card.Difficulty, 4);
        Assert.Equal(T0, card.LastReview);
        Assert.Equal(T0.AddDays(days), card.Due);
        Assert.Equal((1, rating == Rating.Again ? 1 : 0), (card.Reps, card.Lapses));
    }

    [Fact]
    public void Recalling_on_the_due_day_grows_stability_and_eases_difficulty()
    {
        var first = FsrsScheduler.Schedule(null, Rating.Good, T0);

        var second = FsrsScheduler.Schedule(first, Rating.Good, T0.AddDays(3));

        Assert.Equal(10.7389, second.Stability, 3);
        Assert.Equal(5.2730, second.Difficulty, 3);
        Assert.Equal(T0.AddDays(3 + 11), second.Due);
        Assert.Equal(2, second.Reps);
    }

    [Theory]
    [InlineData(Rating.Hard, 4.92451, 6.03495)]
    [InlineData(Rating.Easy, 25.7936, 4.5110)]
    public void Hard_and_easy_recalls_are_penalised_and_rewarded(Rating rating, double stability, double difficulty)
    {
        var first = FsrsScheduler.Schedule(null, Rating.Good, T0);

        var next = FsrsScheduler.Schedule(first, rating, T0.AddDays(3));

        Assert.Equal(stability, next.Stability, 3);
        Assert.Equal(difficulty, next.Difficulty, 3);
    }

    [Fact]
    public void Forgetting_is_a_lapse_that_cuts_stability_and_raises_difficulty()
    {
        var first = FsrsScheduler.Schedule(null, Rating.Good, T0);

        var lapsed = FsrsScheduler.Schedule(first, Rating.Again, T0.AddDays(3));

        Assert.Equal(1.0556, lapsed.Stability, 3);
        Assert.Equal(6.7969, lapsed.Difficulty, 3);
        Assert.Equal(1, lapsed.Lapses);
        Assert.Equal(T0.AddDays(3 + 1), lapsed.Due);
        Assert.True(lapsed.Stability < first.Stability);
    }

    [Theory]
    [InlineData(Rating.Again, 1.5898)]
    [InlineData(Rating.Hard, 2.6648)]
    [InlineData(Rating.Good, 4.4669)]
    [InlineData(Rating.Easy, 7.4875)]
    public void A_second_review_within_the_same_day_uses_the_short_term_rule(Rating rating, double stability)
    {
        var first = FsrsScheduler.Schedule(null, Rating.Good, T0);

        var next = FsrsScheduler.Schedule(first, rating, T0.AddHours(2));

        Assert.Equal(stability, next.Stability, 3);
    }

    [Fact]
    public void Retrievability_is_ninety_percent_after_one_stability_of_time_and_falls_from_there()
    {
        var card = FsrsScheduler.Schedule(null, Rating.Good, T0);

        Assert.Equal(1.0, FsrsScheduler.Retrievability(card, T0), 6);
        Assert.Equal(0.9, FsrsScheduler.Retrievability(card, T0.AddDays(card.Stability)), 6);
        Assert.True(FsrsScheduler.Retrievability(card, T0.AddDays(60)) < 0.5);
    }

    [Fact]
    public void Reviewing_late_rewards_a_recall_more_than_reviewing_on_time()
    {
        var first = FsrsScheduler.Schedule(null, Rating.Good, T0);

        var onTime = FsrsScheduler.Schedule(first, Rating.Good, T0.AddDays(3));
        var late = FsrsScheduler.Schedule(first, Rating.Good, T0.AddDays(10));

        Assert.True(late.Stability > onTime.Stability);
    }

    [Fact]
    public void Difficulty_and_intervals_stay_within_their_limits()
    {
        var card = FsrsScheduler.Schedule(null, Rating.Again, T0);
        for (var i = 1; i <= 60; i++) card = FsrsScheduler.Schedule(card, Rating.Again, T0.AddDays(i));
        Assert.InRange(card.Difficulty, 1, 10);

        for (var i = 1; i <= 60; i++) card = FsrsScheduler.Schedule(card, Rating.Easy, card.Due);
        Assert.InRange(card.Difficulty, 1, 10);
        Assert.InRange((card.Due - card.LastReview).TotalDays, 1, 36500);
    }

    [Fact]
    public void Scheduling_is_a_pure_function()
    {
        var first = FsrsScheduler.Schedule(null, Rating.Good, T0);
        Assert.Equal(FsrsScheduler.Schedule(first, Rating.Hard, T0.AddDays(2)), FsrsScheduler.Schedule(first, Rating.Hard, T0.AddDays(2)));
    }
}
