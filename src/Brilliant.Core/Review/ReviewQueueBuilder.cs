using Brilliant.Core.Content;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Review;

/// <param name="DailyCap">The most reviews offered in one day, answered ones included. A missed day never becomes an overwhelming backlog.</param>
/// <param name="PatternWeight">How many times more urgent a pattern-recognition item counts than a concept item.</param>
/// <param name="ResolveWeight">The same for a re-solve of a problem the learner got wrong.</param>
public sealed record ReviewQueueOptions(int DailyCap = 20, double PatternWeight = 2.0, double ResolveWeight = 1.5)
{
    public double WeightOf(ReviewKind kind) => kind switch
    {
        ReviewKind.Pattern => PatternWeight,
        ReviewKind.Resolve => ResolveWeight,
        _ => 1.0,
    };
}

/// <summary>Today's review queue.</summary>
/// <param name="Items">What to review now, most important first. Never more than the cap leaves room for.</param>
/// <param name="Waiting">Items that are due but didn't fit under today's cap. They stay due and are offered when room opens up.</param>
/// <param name="DoneToday">Reviews already answered today; they use up the cap.</param>
public sealed record ReviewQueue(IReadOnlyList<ReviewItemState> Items, int Waiting, int DoneToday)
{
    public static ReviewQueue Empty { get; } = new([], 0, 0);
}

/// <summary>
/// Chooses what Review shows today. Pure: the same items, time and options always give the same queue.
/// <list type="number">
/// <item>Only items that are due now: unlocked, and new or past their due time.</item>
/// <item>Items already scheduled come before new ones. Among them, the most forgotten comes first:
/// <c>weight × (1 − retrievability)</c>, so a long-overdue item outranks a fresh one, and a pattern item counts as
/// <see cref="ReviewQueueOptions.PatternWeight"/> times as forgotten as it is.</item>
/// <item>New items follow, heaviest kind first, then in content order.</item>
/// <item>At most <see cref="ReviewQueueOptions.DailyCap"/> reviews a day, counting those already answered today.</item>
/// </list>
/// </summary>
public static class ReviewQueueBuilder
{
    /// <param name="reviews">Every review item, in content order (the order breaks ties).</param>
    /// <param name="now">The moment the queue is for.</param>
    /// <param name="answeredToday">How many reviews were answered earlier today.</param>
    public static ReviewQueue Build(IEnumerable<ReviewItemState> reviews, DateTimeOffset now, int answeredToday, ReviewQueueOptions? options = null)
    {
        options ??= new ReviewQueueOptions();
        var due = reviews.Where(r => r.IsDue(now)).ToList();

        var scheduled = due.Where(r => r.Card is not null)
            .OrderByDescending(r => options.WeightOf(r.Item.Kind) * (1 - FsrsScheduler.Retrievability(r.Card!, now)));
        var fresh = due.Where(r => r.Card is null)
            .OrderByDescending(r => options.WeightOf(r.Item.Kind));

        var room = Math.Max(0, options.DailyCap - Math.Max(0, answeredToday));
        var items = scheduled.Concat(fresh).Take(room).ToList();
        return new ReviewQueue(items, due.Count - items.Count, answeredToday);
    }
}
