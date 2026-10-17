using System.Text.Json;
using Brilliant.Core.Content;

namespace Brilliant.Core.Tests;

/// <summary>The interleaving machine: hand-stepped threads graded against an authored invariant (#162).</summary>
public sealed class InterleaveMachineTests
{
    private static InterleaveStep Counter() => new(
        "step.interleave.counter", "Lose an update", "Break the counter.",
        [new("counter", 0)],
        [
            new("A", [new("var t = counter;", "t = counter"), new("counter = t + 1;", "counter = t + 1")]),
            new("B", [new("var t = counter;", "t = counter"), new("counter = t + 1;", "counter = t + 1")]),
        ],
        new(InterleaveInvariant.FinalValue, "counter is 2 once both have run", "counter", 2),
        ["A", "B", "A", "B"], [], "Both read 0.");

    private static InterleaveStep Locks() => new(
        "step.interleave.locks", "Deadlock", "Make them wait for each other.",
        [],
        [
            new("A", [new("lock (a) {", "lock a"), new("lock (b) {", "lock b"), new("}", "unlock b"), new("}", "unlock a")]),
            new("B", [new("lock (b) {", "lock b"), new("lock (a) {", "lock a"), new("}", "unlock a"), new("}", "unlock b")]),
        ],
        new(InterleaveInvariant.NoDeadlock, "the threads always finish"),
        ["A", "B", "A", "B"], [], "Each holds the lock the other wants.");

    [Fact]
    public void Interleaved_load_and_store_lose_an_update()
    {
        var result = InterleaveMachine.Evaluate(Counter(), ["A", "B", "A", "B"]);

        Assert.True(result.BreaksInvariant);
        Assert.Equal(InterleaveOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.States[^1].Shared["counter"]);
    }

    [Fact]
    public void Running_one_thread_after_the_other_keeps_the_count()
    {
        var result = InterleaveMachine.Evaluate(Counter(), ["A", "A", "B", "B"]);

        Assert.False(result.BreaksInvariant);
        Assert.Equal(2, result.States[^1].Shared["counter"]);
        Assert.Contains("counter ended at 2", result.Feedback);
    }

    [Fact]
    public void Each_step_adds_a_state_and_the_first_is_the_start()
    {
        var result = InterleaveMachine.Evaluate(Counter(), ["A", "B", "A", "B"]);

        Assert.Equal(5, result.States.Count);
        Assert.Equal(0, result.States[0].Shared["counter"]);
        Assert.Equal(0, result.States[2].Locals[1]["t"]);
        Assert.Equal(1, result.States[3].Shared["counter"]);
    }

    [Fact]
    public void Opposite_lock_orders_deadlock()
    {
        var result = InterleaveMachine.Evaluate(Locks(), ["A", "B"]);

        Assert.True(result.BreaksInvariant);
        Assert.Equal(InterleaveOutcome.Deadlocked, result.Outcome);
    }

    [Fact]
    public void The_same_lock_order_does_not_deadlock_in_any_order_that_finishes()
    {
        var result = InterleaveMachine.Evaluate(Locks(), ["A", "A", "A", "A", "B", "B", "B", "B"]);

        Assert.False(result.BreaksInvariant);
        Assert.Equal(InterleaveOutcome.Completed, result.Outcome);
        Assert.Contains("no deadlock", result.Feedback);
    }

    [Fact]
    public void A_blocked_thread_cannot_be_scheduled()
    {
        var step = Locks();
        var state = InterleaveMachine.Advance(step, InterleaveMachine.Start(step), 0);

        Assert.Equal(InterleaveThreadStatus.Ready, InterleaveMachine.StatusOf(step, state, 1));
        state = InterleaveMachine.Advance(step, state, 1);
        Assert.Equal(InterleaveThreadStatus.Blocked, InterleaveMachine.StatusOf(step, state, 0));
        Assert.Throws<InvalidOperationException>(() => InterleaveMachine.Advance(step, state, 0));

        var result = InterleaveMachine.Evaluate(step, ["A", "B", "A"]);
        Assert.False(result.BreaksInvariant);
        Assert.Contains("waiting for a lock", result.Feedback);
    }

    [Fact]
    public void A_thread_waits_only_for_a_lock_someone_else_holds()
    {
        var step = Locks();
        var state = InterleaveMachine.Advance(step, InterleaveMachine.Start(step), 0);
        state = InterleaveMachine.Advance(step, state, 0);

        Assert.Equal(InterleaveThreadStatus.Ready, InterleaveMachine.StatusOf(step, state, 0));
        Assert.Equal(InterleaveThreadStatus.Blocked, InterleaveMachine.StatusOf(step, state, 1));
    }

    [Fact]
    public void A_schedule_that_stops_early_is_not_a_break()
    {
        var result = InterleaveMachine.Evaluate(Counter(), ["A", "B", "A"]);

        Assert.False(result.BreaksInvariant);
        Assert.Equal(InterleaveOutcome.Running, result.Outcome);
        Assert.Contains("stops before", result.Feedback);
    }

    [Fact]
    public void Naming_a_finished_or_unknown_thread_is_rejected_with_the_step_number()
    {
        Assert.Contains("Step 3: thread A has already finished", InterleaveMachine.Evaluate(Counter(), ["A", "A", "A"]).Feedback);
        Assert.Contains("Step 1: there is no thread 'C'", InterleaveMachine.Evaluate(Counter(), ["C"]).Feedback);
    }

    [Fact]
    public void An_empty_schedule_has_not_run_anything()
    {
        var result = InterleaveMachine.Evaluate(Counter(), []);

        Assert.False(result.BreaksInvariant);
        Assert.Single(result.States);
    }

    [Theory]
    [InlineData("t = counter", InterleaveInstructionKind.Load, "t", "counter", 0)]
    [InlineData("counter = t", InterleaveInstructionKind.Store, "counter", "t", 0)]
    [InlineData("counter = t + 1", InterleaveInstructionKind.Store, "counter", "t", 1)]
    [InlineData("counter = t - 3", InterleaveInstructionKind.Store, "counter", "t", -3)]
    [InlineData("counter = 5", InterleaveInstructionKind.Store, "counter", null, 5)]
    [InlineData("lock gate", InterleaveInstructionKind.Lock, "gate", null, 0)]
    [InlineData("unlock gate", InterleaveInstructionKind.Unlock, "gate", null, 0)]
    public void Machine_lines_parse(string text, InterleaveInstructionKind kind, string name, string? source, int offset)
    {
        Assert.True(InterleaveMachine.TryParse(text, new HashSet<string> { "counter", "other" }, out var instruction, out _));
        Assert.Equal(new InterleaveInstruction(kind, name, source, offset), instruction);
    }

    [Theory]
    [InlineData("t = u")]
    [InlineData("counter = other")]
    [InlineData("t = counter + 1")]
    [InlineData("counter++")]
    [InlineData("lock")]
    [InlineData("")]
    public void Lines_outside_the_machine_language_are_rejected(string text)
    {
        Assert.False(InterleaveMachine.TryParse(text, new HashSet<string> { "counter", "other" }, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void The_step_round_trips_through_the_pack_json()
    {
        var json = JsonSerializer.Serialize<Step>(Counter());
        var back = Assert.IsType<InterleaveStep>(JsonSerializer.Deserialize<Step>(json));

        Assert.Contains("\"type\":\"interleave\"", json);
        Assert.Equal(2, back.Threads.Count);
        Assert.Equal("counter", back.Invariant.Variable);
    }

    [Theory]
    [InlineData("counter = t + 2147483648")]
    [InlineData("counter = t - 99999999999")]
    [InlineData("counter = 2147483648")]
    public void A_number_too_large_is_an_error_not_a_crash(string line)
    {
        Assert.False(InterleaveMachine.TryParse(line, new HashSet<string> { "counter" }, out _, out var error));
        Assert.Contains("too large", error);
    }

    [Fact]
    public void The_largest_int_still_parses()
    {
        Assert.True(InterleaveMachine.TryParse("counter = t + 2147483647", new HashSet<string> { "counter" }, out var instruction, out _));
        Assert.Equal(int.MaxValue, instruction.Offset);
    }
}
