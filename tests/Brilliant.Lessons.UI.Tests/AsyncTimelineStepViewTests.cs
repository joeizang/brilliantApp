using Brilliant.Core.Content;
using Bunit;

namespace Brilliant.Lessons.UI.Tests;

public sealed class AsyncTimelineStepViewTests : BunitContext
{
    private static readonly AsyncTimelineStep DeadlockingStep = new("step.async", "Where does the continuation run?", "Watch the operation complete.",
    [
        new(1, "UI", AsyncTimelineEventKind.Enter, "LoadAsync starts", State: "Start"),
        new(2, "UI", AsyncTimelineEventKind.Await, "Await load", Operation: "load", State: "AwaitingLoad"),
        new(3, "UI", AsyncTimelineEventKind.BlockingWait, "Result blocks the UI thread", Operation: "load"),
        new(4, "Worker", AsyncTimelineEventKind.ContinuationQueued, "Queue the continuation", Operation: "load", ContinuationThread: "UI", State: "AwaitingLoad"),
        new(5, "UI", AsyncTimelineEventKind.Resume, "Resume after await", State: "AfterAwait"),
    ]);

    [Fact]
    public void Timeline_shows_thread_lanes_await_state_and_the_sync_over_async_cycle()
    {
        var cut = Render<AsyncTimelineStepView>(p => p.Add(c => c.Step, DeadlockingStep));

        Assert.Equal(2, cut.FindAll(".async-lane").Count);
        Assert.Contains("UI", cut.Find(".async-lane[aria-label='Thread UI']").TextContent);
        Assert.Contains("Worker", cut.Find(".async-lane[aria-label='Thread Worker']").TextContent);
        Assert.Contains("AwaitingLoad", cut.Markup);
        Assert.Contains("Continuation resumes on", cut.Find(".async-lane[aria-label='Thread Worker']").TextContent);
        Assert.Contains("Sync-over-async deadlock", cut.Markup);
        Assert.Contains("cannot finish until the blocked thread runs it", cut.Markup);
        Assert.Equal("1", cut.FindAll(".async-event-order")[0].TextContent);
    }

    [Fact]
    public void Timeline_does_not_label_a_continuation_on_an_unblocked_thread_as_a_deadlock()
    {
        var step = DeadlockingStep with
        {
            Events = DeadlockingStep.Events.Select(e => e with { ContinuationThread = e.Kind == AsyncTimelineEventKind.ContinuationQueued ? "Worker" : e.ContinuationThread }).ToArray(),
        };

        var cut = Render<AsyncTimelineStepView>(p => p.Add(c => c.Step, step));

        Assert.Empty(cut.FindAll(".async-deadlock"));
        Assert.Contains("Worker", cut.Find(".async-lane[aria-label='Thread Worker']").TextContent);
    }

}
