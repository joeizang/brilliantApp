using Brilliant.Core.Progress;
using static Brilliant.Core.Tests.SyncFixtures;

namespace Brilliant.Core.Tests;

/// <summary>
/// Sync hands each device the other's events in whatever order they arrive, so what a learner sees must depend on the set of events
/// and nothing else. If any of this fails, two devices that hold the same events can show different progress.
/// </summary>
public class EventOrderTests
{
    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static List<ProgressEvent> History()
    {
        var log = new MemoryLog();
        var clock = new Clock(T0);
        TwoDevicesApart(new ProgressRecorder(log, "device-a", clock), new ProgressRecorder(log, "device-b", clock), clock);
        return log.Items;
    }

    private static string Fingerprint(IEnumerable<ProgressEvent> events) => LearnerStateFingerprint.Of(events, SyncFixtures.Content, T0.AddDays(2));

    [Fact]
    public void The_fixture_history_is_rich_enough_to_mean_something()
    {
        var events = History();
        Assert.True(events.Count >= 20, $"only {events.Count} events");
        Assert.Equal(2, events.Select(e => e.DeviceId).Distinct().Count());
        Assert.Contains(events.GroupBy(e => e.OccurredAt), g => g.Select(e => e.DeviceId).Distinct().Count() > 1);   // ties across devices exist

        var print = Fingerprint(events);
        Assert.Contains("lesson lesson.one Completed", print);
        Assert.Contains("goal=12", print);
        Assert.DoesNotContain("card=new", print.Split('\n').Single(l => l.StartsWith("review review.one.q1")));
    }

    [Fact]
    public void Any_order_of_the_same_events_gives_the_same_learner_state()
    {
        var events = History();
        var expected = Fingerprint(events);
        var random = new Random(2910);

        for (var i = 0; i < 200; i++)
        {
            var shuffled = events.OrderBy(_ => random.Next()).ToList();
            Assert.Equal(expected, Fingerprint(shuffled));
        }
    }

    [Fact]
    public void The_reverse_order_gives_the_same_learner_state()
    {
        var events = History();
        Assert.Equal(Fingerprint(events), Fingerprint(Enumerable.Reverse(events)));
    }

    [Fact]
    public void Receiving_events_twice_changes_nothing()
    {
        var events = History();
        Assert.Equal(Fingerprint(events), Fingerprint(events.Concat(events)));
    }

    [Fact]
    public void Each_device_alone_sees_less_than_both_together()
    {
        var events = History();
        var both = Fingerprint(events);
        Assert.NotEqual(both, Fingerprint(events.Where(e => e.DeviceId == "device-a")));
        Assert.NotEqual(both, Fingerprint(events.Where(e => e.DeviceId == "device-b")));
    }
}
