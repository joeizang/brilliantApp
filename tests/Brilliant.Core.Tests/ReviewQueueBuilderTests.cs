using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>The Review Queue Builder is a pure function: which due items to show today, in what order, under the daily cap.</summary>
public class ReviewQueueBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly ChoiceStep Question = new("step.q", "Q", "?", false, [new ChoiceOption("a", true, null)]);
    private static readonly Lesson Lesson = new("lesson.l", "L", "track.t", [Question]);

    private static ReviewItemState Item(string id, ReviewKind kind = ReviewKind.Concept, CardState? card = null, bool unlocked = true) =>
        new(Lesson, new ReviewItem(id, "concept.c", "step.q", kind), Question, unlocked, card, card is null ? 0 : 1);

    /// <summary>A card answered "Good" at <paramref name="reviewedAt"/>: due three days later, about 90% recallable at that moment.</summary>
    private static CardState Reviewed(DateTimeOffset reviewedAt) => FsrsScheduler.Schedule(null, Rating.Good, reviewedAt);

    private static string[] Ids(ReviewQueue queue) => queue.Items.Select(i => i.Item.Id).ToArray();

    private static ReviewQueue Build(IEnumerable<ReviewItemState> items, DateTimeOffset? now = null, int answeredToday = 0, ReviewQueueOptions? options = null) =>
        ReviewQueueBuilder.Build(items, now ?? T0.AddDays(10), answeredToday, options);

    // --- What is offered -------------------------------------------------------------------------------------------

    [Fact]
    public void Only_due_unlocked_items_are_offered()
    {
        var now = T0.AddDays(10);
        var queue = Build([
            Item("locked", unlocked: false),
            Item("not-yet", card: Reviewed(now.AddDays(-1))),
            Item("new"),
            Item("due", card: Reviewed(now.AddDays(-5)))], now);

        Assert.Equal(["due", "new"], Ids(queue));
        Assert.Equal(0, queue.Waiting);
    }

    [Fact]
    public void Nothing_to_review_gives_an_empty_queue()
    {
        var queue = Build([]);

        Assert.Empty(queue.Items);
        Assert.Equal(0, queue.Waiting);
        Assert.Equal(0, queue.DoneToday);
    }

    // --- The daily cap ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_cap_defaults_to_twenty_and_the_rest_are_reported_as_waiting()
    {
        var queue = Build(Enumerable.Range(0, 30).Select(i => Item($"item{i:00}")));

        Assert.Equal(20, queue.Items.Count);
        Assert.Equal(10, queue.Waiting);
    }

    [Fact]
    public void Reviews_already_answered_today_use_up_the_cap()
    {
        var items = Enumerable.Range(0, 30).Select(i => Item($"item{i:00}")).ToList();

        var queue = Build(items, answeredToday: 15);

        Assert.Equal(5, queue.Items.Count);
        Assert.Equal(25, queue.Waiting);
        Assert.Equal(15, queue.DoneToday);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(35)]
    public void Once_the_cap_is_reached_nothing_more_is_offered_today(int answeredToday)
    {
        var queue = Build([Item("a"), Item("b")], answeredToday: answeredToday);

        Assert.Empty(queue.Items);
        Assert.Equal(2, queue.Waiting);
    }

    [Fact]
    public void The_cap_is_configurable_and_zero_offers_nothing()
    {
        var items = Enumerable.Range(0, 10).Select(i => Item($"item{i}")).ToList();

        Assert.Equal(3, Build(items, options: new ReviewQueueOptions(DailyCap: 3)).Items.Count);
        Assert.Empty(Build(items, options: new ReviewQueueOptions(DailyCap: 0)).Items);
    }

    // --- Overdue items first ---------------------------------------------------------------------------------------

    [Fact]
    public void The_most_overdue_item_comes_first_whatever_the_content_order()
    {
        var now = T0.AddDays(30);
        var queue = Build([
            Item("recent", card: Reviewed(now.AddDays(-5))),
            Item("ancient", card: Reviewed(now.AddDays(-25))),
            Item("middling", card: Reviewed(now.AddDays(-12)))], now);

        Assert.Equal(["ancient", "middling", "recent"], Ids(queue));
    }

    [Fact]
    public void Items_already_learned_come_before_new_ones()
    {
        var now = T0.AddDays(10);
        var queue = Build([Item("new1"), Item("new2"), Item("due", card: Reviewed(now.AddDays(-4)))], now);

        Assert.Equal(["due", "new1", "new2"], Ids(queue));
    }

    [Fact]
    public void When_the_cap_bites_overdue_items_are_kept_and_new_ones_wait()
    {
        var now = T0.AddDays(30);
        var queue = Build([
            Item("new1"),
            Item("old1", card: Reviewed(now.AddDays(-20))),
            Item("new2"),
            Item("old2", card: Reviewed(now.AddDays(-15))),
            Item("old3", card: Reviewed(now.AddDays(-10)))], now, options: new ReviewQueueOptions(DailyCap: 4));

        Assert.Equal(["old1", "old2", "old3", "new1"], Ids(queue));
        Assert.Equal(1, queue.Waiting);
    }

    [Fact]
    public void How_overdue_depends_on_the_items_own_memory_not_just_the_calendar()
    {
        // Both are 2 days past due, but one had a 100-day memory and the other a 3-day one: the short one is far more forgotten.
        var now = T0.AddDays(200);
        var strong = new CardState(Stability: 100, Difficulty: 5, LastReview: now.AddDays(-102), Due: now.AddDays(-2), Reps: 5, Lapses: 0);
        var weak = new CardState(Stability: 3, Difficulty: 5, LastReview: now.AddDays(-5), Due: now.AddDays(-2), Reps: 1, Lapses: 0);

        Assert.Equal(["weak", "strong"], Ids(Build([Item("strong", card: strong), Item("weak", card: weak)], now)));
    }

    // --- Pattern recognition is weighted up ------------------------------------------------------------------------

    [Fact]
    public void A_pattern_item_beats_a_concept_item_that_is_equally_overdue()
    {
        var now = T0.AddDays(10);
        var card = Reviewed(now.AddDays(-6));

        var queue = Build([Item("concept", card: card), Item("pattern", ReviewKind.Pattern, card)], now, options: new ReviewQueueOptions(DailyCap: 1));

        Assert.Equal(["pattern"], Ids(queue));
        Assert.Equal(1, queue.Waiting);
    }

    [Fact]
    public void Weighting_is_not_absolute_a_far_more_forgotten_concept_still_wins()
    {
        var now = T0.AddDays(30);
        var pattern = Item("pattern", ReviewKind.Pattern, Reviewed(now.AddDays(-3)));   // just due: about 90% recallable
        var concept = Item("concept", card: Reviewed(now.AddDays(-25)));                // long forgotten

        Assert.Equal(["concept", "pattern"], Ids(Build([pattern, concept], now)));
    }

    [Fact]
    public void A_pattern_item_overtakes_a_moderately_more_overdue_concept()
    {
        var now = T0.AddDays(30);
        var pattern = Item("pattern", ReviewKind.Pattern, Reviewed(now.AddDays(-8)));
        var concept = Item("concept", card: Reviewed(now.AddDays(-11)));

        Assert.Equal(["pattern", "concept"], Ids(Build([concept, pattern], now)));
    }

    [Fact]
    public void Among_new_items_patterns_come_first_then_re_solves_then_concepts_each_in_content_order()
    {
        var queue = Build([
            Item("concept1"),
            Item("resolve1", ReviewKind.Resolve),
            Item("pattern1", ReviewKind.Pattern),
            Item("concept2"),
            Item("pattern2", ReviewKind.Pattern),
            Item("resolve2", ReviewKind.Resolve)]);

        Assert.Equal(["pattern1", "pattern2", "resolve1", "resolve2", "concept1", "concept2"], Ids(queue));
    }

    [Fact]
    public void Weights_are_configurable()
    {
        var items = new[] { Item("concept"), Item("pattern", ReviewKind.Pattern) };

        Assert.Equal(["concept", "pattern"], Ids(Build(items, options: new ReviewQueueOptions(PatternWeight: 1))));
        Assert.Equal(["pattern", "concept"], Ids(Build(items, options: new ReviewQueueOptions(PatternWeight: 3))));
    }

    // --- Re-solves -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_re_solve_is_offered_ahead_of_new_concept_items_but_behind_items_already_learned()
    {
        var now = T0.AddDays(10);
        var queue = Build([Item("concept"), Item("resolve", ReviewKind.Resolve), Item("due", card: Reviewed(now.AddDays(-4)))], now);

        Assert.Equal(["due", "resolve", "concept"], Ids(queue));
    }

    // --- Purity ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Building_twice_gives_the_same_queue_and_leaves_the_input_alone()
    {
        var now = T0.AddDays(30);
        var items = new List<ReviewItemState>
        {
            Item("a", card: Reviewed(now.AddDays(-9))), Item("b", ReviewKind.Pattern), Item("c", card: Reviewed(now.AddDays(-20))), Item("d"),
        };
        var before = items.Select(i => i.Item.Id).ToList();

        var first = Build(items, now, answeredToday: 1, new ReviewQueueOptions(DailyCap: 3));
        var second = Build(items, now, answeredToday: 1, new ReviewQueueOptions(DailyCap: 3));

        Assert.Equal(Ids(first), Ids(second));
        Assert.Equal((first.Waiting, first.DoneToday), (second.Waiting, second.DoneToday));
        Assert.Equal(before, items.Select(i => i.Item.Id));
    }
}
