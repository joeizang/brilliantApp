namespace Brilliant.Lessons.UI;

/// <summary>What a keyboard shortcut or menu command asks the current step to do.</summary>
public enum StepCommand
{
    /// <summary>Run the code, or check the answer (⌘↩). Never moves on to the next step, so it is safe to arrive twice.</summary>
    Run,

    /// <summary>Do the step's main action: check, continue or try again (Return, outside text boxes).</summary>
    Advance,

    /// <summary>Pick answer <see cref="StepShortcut.Number"/> (keys 1–4).</summary>
    Choose,

    /// <summary>Leave the lesson.</summary>
    Back,
}

/// <param name="Number">The 1-based answer for <see cref="StepCommand.Choose"/>; 0 otherwise.</param>
public readonly record struct StepShortcut(StepCommand Command, int Number = 0)
{
    /// <summary>The highest answer number reachable from the keyboard.</summary>
    public const int MaxChoice = 4;
}

/// <summary>
/// Carries shortcuts from where they happen (the page's key listener, the native menu bar) to whichever step is on screen.
/// Step views subscribe while they are mounted, so there is no routing: with one step showing, whoever is listening is the target.
/// </summary>
public sealed class StepShortcuts
{
    private readonly List<Func<StepShortcut, Task>> _handlers = [];

    /// <summary>Listens until the returned handle is disposed.</summary>
    public IDisposable Subscribe(Func<StepShortcut, Task> handler)
    {
        lock (_handlers) _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    public async Task RaiseAsync(StepShortcut shortcut)
    {
        Func<StepShortcut, Task>[] snapshot;
        lock (_handlers) snapshot = [.. _handlers];
        foreach (var handler in snapshot) await handler(shortcut);
    }

    private sealed class Subscription(StepShortcuts owner, Func<StepShortcut, Task> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._handlers) owner._handlers.Remove(handler);
        }
    }
}
