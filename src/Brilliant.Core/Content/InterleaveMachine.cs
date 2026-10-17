using System.Globalization;
using System.Text.RegularExpressions;

namespace Brilliant.Core.Content;

public enum InterleaveThreadStatus { Ready, Blocked, Done }

/// <summary>Where a run stands: still going, every thread finished, or every unfinished thread waiting for a lock another holds.</summary>
public enum InterleaveOutcome { Running, Completed, Deadlocked }

public enum InterleaveInstructionKind { Load, Store, Lock, Unlock }

/// <summary>
/// A parsed machine line. A <see cref="InterleaveInstructionKind.Load"/> copies the shared variable <see cref="Source"/> into the thread's
/// local <see cref="Name"/>; a <see cref="InterleaveInstructionKind.Store"/> writes the shared variable <see cref="Name"/> with the
/// value of local <see cref="Source"/> (none: zero) plus <see cref="Offset"/>; lock and unlock name the lock in <see cref="Name"/>.
/// </summary>
public sealed record InterleaveInstruction(InterleaveInstructionKind Kind, string Name, string? Source = null, int Offset = 0);

/// <summary>Everything a run holds at one moment. Immutable: every step makes a new state, so undo and replay are just indexes into a list of them.</summary>
public sealed record InterleaveState(
    IReadOnlyDictionary<string, int> Shared,
    IReadOnlyList<int> Pcs,
    IReadOnlyList<IReadOnlyDictionary<string, int>> Locals,
    IReadOnlyDictionary<string, string> LockHolders);

/// <summary>
/// The verdict on a schedule: <see cref="BreaksInvariant"/> is the answer. <see cref="States"/> holds the start state followed by one
/// state per step taken, up to the first step that could not be taken. <see cref="Feedback"/> says why it is not a break.
/// </summary>
public sealed record InterleaveResult(bool BreaksInvariant, InterleaveOutcome Outcome, IReadOnlyList<InterleaveState> States, string? Feedback);

/// <summary>
/// A tiny interpreter for hand-stepped threads. A line is one of
/// <c>local = shared</c>, <c>shared = local</c>, <c>shared = local + 1</c>, <c>shared = 5</c>, <c>lock name</c>, <c>unlock name</c>;
/// each is one indivisible step, so a C# line like <c>counter++</c> is authored as the load and the store it really is.
/// Pure: no clock, no I/O, no state outside the values passed in.
/// </summary>
public static partial class InterleaveMachine
{
    [GeneratedRegex(@"^(?<name>[A-Za-z_]\w*) = (?<local>[A-Za-z_]\w*)(?: (?<sign>[+-]) (?<n>\d+))?$", RegexOptions.None, 100)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^(?<name>[A-Za-z_]\w*) = (?<n>-?\d+)$", RegexOptions.None, 100)]
    private static partial Regex Constant();

    [GeneratedRegex(@"^(?<kind>lock|unlock) (?<name>[A-Za-z_]\w*)$", RegexOptions.None, 100)]
    private static partial Regex LockLine();

    /// <summary>Parses one line. <paramref name="shared"/> says which names are shared variables: an assignment to one is a store, from one is a load.</summary>
    public static bool TryParse(string? text, IReadOnlySet<string> shared, out InterleaveInstruction instruction, out string error)
    {
        instruction = new(InterleaveInstructionKind.Lock, "");
        error = "";
        var line = (text ?? "").Trim();

        if (LockLine().Match(line) is { Success: true } l)
        {
            instruction = new(l.Groups["kind"].Value == "lock" ? InterleaveInstructionKind.Lock : InterleaveInstructionKind.Unlock, l.Groups["name"].Value);
            return true;
        }
        if (Constant().Match(line) is { Success: true } c)
        {
            if (!shared.Contains(c.Groups["name"].Value)) { error = $"'{c.Groups["name"].Value}' is not a shared variable, so a constant cannot be stored in it."; return false; }
            if (!int.TryParse(c.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var constant)) { error = $"'{line}': the number is too large."; return false; }
            instruction = new(InterleaveInstructionKind.Store, c.Groups["name"].Value, null, constant);
            return true;
        }
        if (Assignment().Match(line) is { Success: true } a)
        {
            var target = a.Groups["name"].Value;
            var source = a.Groups["local"].Value;
            var hasOffset = a.Groups["n"].Success;
            if (shared.Contains(target))
            {
                if (shared.Contains(source)) { error = $"'{line}' reads one shared variable into another in a single step; load it into a local first."; return false; }
                var offset = 0;
                if (hasOffset)
                {
                    if (!int.TryParse(a.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out offset)) { error = $"'{line}': the number is too large."; return false; }
                    if (a.Groups["sign"].Value == "-") offset = -offset;
                }
                instruction = new(InterleaveInstructionKind.Store, target, source, offset);
                return true;
            }
            if (!shared.Contains(source) || hasOffset) { error = $"'{line}' must load a shared variable into a local (local = shared), or store into a shared one."; return false; }
            instruction = new(InterleaveInstructionKind.Load, target, source);
            return true;
        }
        error = $"'{line}' is not a machine line: use 'local = shared', 'shared = local', 'shared = local + 1', 'shared = 5', 'lock name' or 'unlock name'.";
        return false;
    }

    /// <summary>The step's lines parsed, one list per thread. Throws on a line that does not parse: the pack build rejects those, so a built pack has none.</summary>
    public static IReadOnlyList<IReadOnlyList<InterleaveInstruction>> Program(InterleaveStep step)
    {
        var shared = step.Shared.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        return step.Threads.Select(t => (IReadOnlyList<InterleaveInstruction>)t.Ops.Select(op =>
            TryParse(op.Do, shared, out var instruction, out var error) ? instruction : throw new InvalidOperationException($"Step '{step.Id}': {error}")).ToList()).ToList();
    }

    public static InterleaveState Start(InterleaveStep step) => new(
        step.Shared.ToDictionary(v => v.Name, v => v.Initial, StringComparer.Ordinal),
        step.Threads.Select(_ => 0).ToList(),
        step.Threads.Select(_ => (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.Ordinal)).ToList(),
        new Dictionary<string, string>(StringComparer.Ordinal));

    public static InterleaveThreadStatus StatusOf(InterleaveStep step, InterleaveState state, int thread) =>
        StatusOf(Program(step), step, state, thread);

    private static InterleaveThreadStatus StatusOf(IReadOnlyList<IReadOnlyList<InterleaveInstruction>> program, InterleaveStep step, InterleaveState state, int thread)
    {
        if (state.Pcs[thread] >= program[thread].Count) return InterleaveThreadStatus.Done;
        var next = program[thread][state.Pcs[thread]];
        return next.Kind == InterleaveInstructionKind.Lock && state.LockHolders.TryGetValue(next.Name, out var holder) && holder != step.Threads[thread].Name
            ? InterleaveThreadStatus.Blocked
            : InterleaveThreadStatus.Ready;
    }

    public static InterleaveOutcome OutcomeOf(InterleaveStep step, InterleaveState state) => OutcomeOf(Program(step), step, state);

    private static InterleaveOutcome OutcomeOf(IReadOnlyList<IReadOnlyList<InterleaveInstruction>> program, InterleaveStep step, InterleaveState state)
    {
        var statuses = Enumerable.Range(0, step.Threads.Count).Select(i => StatusOf(program, step, state, i)).ToList();
        if (statuses.All(s => s == InterleaveThreadStatus.Done)) return InterleaveOutcome.Completed;
        return statuses.Contains(InterleaveThreadStatus.Ready) ? InterleaveOutcome.Running : InterleaveOutcome.Deadlocked;
    }

    /// <summary>Runs thread <paramref name="thread"/>'s next line. Throws if that thread is finished or waiting for a lock: the player never offers it.</summary>
    public static InterleaveState Advance(InterleaveStep step, InterleaveState state, int thread) =>
        Advance(Program(step), step, state, thread);

    private static InterleaveState Advance(IReadOnlyList<IReadOnlyList<InterleaveInstruction>> program, InterleaveStep step, InterleaveState state, int thread)
    {
        if (StatusOf(program, step, state, thread) != InterleaveThreadStatus.Ready)
            throw new InvalidOperationException($"Thread '{step.Threads[thread].Name}' cannot take a step now.");

        var instruction = program[thread][state.Pcs[thread]];
        var shared = new Dictionary<string, int>(state.Shared, StringComparer.Ordinal);
        var locals = state.Locals.Select(l => new Dictionary<string, int>(l, StringComparer.Ordinal)).ToList();
        var locks = new Dictionary<string, string>(state.LockHolders, StringComparer.Ordinal);
        var pcs = state.Pcs.ToList();

        switch (instruction.Kind)
        {
            case InterleaveInstructionKind.Load:
                locals[thread][instruction.Name] = shared[instruction.Source!];
                break;
            case InterleaveInstructionKind.Store:
                shared[instruction.Name] = (instruction.Source is null ? 0 : locals[thread].GetValueOrDefault(instruction.Source)) + instruction.Offset;
                break;
            case InterleaveInstructionKind.Lock:
                locks[instruction.Name] = step.Threads[thread].Name;
                break;
            case InterleaveInstructionKind.Unlock:
                locks.Remove(instruction.Name);
                break;
        }
        pcs[thread]++;
        return new(shared, pcs, locals.Select(l => (IReadOnlyDictionary<string, int>)l).ToList(), locks);
    }

    /// <summary>Whether the run that ended in <paramref name="state"/> violates the step's invariant. A run still going violates nothing yet.</summary>
    public static bool Violates(InterleaveStep step, InterleaveState state)
    {
        var outcome = OutcomeOf(step, state);
        return step.Invariant.Kind switch
        {
            InterleaveInvariant.NoDeadlock => outcome == InterleaveOutcome.Deadlocked,
            InterleaveInvariant.FinalValue => outcome == InterleaveOutcome.Completed && state.Shared[step.Invariant.Variable!] != step.Invariant.Expected,
            _ => false,
        };
    }

    /// <summary>
    /// Runs <paramref name="schedule"/> (thread names, one per step) and judges it. It breaks the invariant only when the run ends
    /// (every thread finished, or all stuck) in a state that violates it; a schedule that stops early, names a finished or blocked thread,
    /// or leaves the invariant intact is not a break, and says why.
    /// </summary>
    public static InterleaveResult Evaluate(InterleaveStep step, IReadOnlyList<string> schedule)
    {
        var program = Program(step);
        var state = Start(step);
        var states = new List<InterleaveState> { state };
        var names = step.Threads.Select(t => t.Name).ToList();

        for (var i = 0; i < schedule.Count; i++)
        {
            var thread = names.IndexOf(schedule[i]);
            if (thread < 0) return Stopped(states, state, $"Step {i + 1}: there is no thread '{schedule[i]}'.");
            switch (StatusOf(program, step, state, thread))
            {
                case InterleaveThreadStatus.Done:
                    return Stopped(states, state, $"Step {i + 1}: thread {schedule[i]} has already finished.");
                case InterleaveThreadStatus.Blocked:
                    return Stopped(states, state, $"Step {i + 1}: thread {schedule[i]} is waiting for a lock the other thread holds, so it cannot run.");
            }
            state = Advance(program, step, state, thread);
            states.Add(state);
        }

        var outcome = OutcomeOf(program, step, state);
        if (outcome == InterleaveOutcome.Running)
            return new(false, outcome, states, "The schedule stops before the threads have finished. Keep stepping until every thread is done, or none can move.");
        if (Violates(step, state)) return new(true, outcome, states, null);
        return new(false, outcome, states, step.Invariant.Kind == InterleaveInvariant.NoDeadlock
            ? "Every thread finished, so there was no deadlock in that order."
            : $"Every thread finished and {step.Invariant.Variable} ended at {state.Shared[step.Invariant.Variable!]}: the invariant held in that order.");
    }

    private static InterleaveResult Stopped(List<InterleaveState> states, InterleaveState last, string feedback) =>
        new(false, InterleaveOutcome.Running, states, feedback);
}
