using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>A bUnit context with the shortcut bus every step view listens on, plus a way to press a shortcut.</summary>
public abstract class ShortcutContext : BunitContext
{
    protected StepShortcuts Shortcuts { get; } = new();

    /// <summary>The clock the Trace Player's playback runs on; it only moves when a test ticks it.</summary>
    protected ManualTimeProvider PlaybackClock { get; } = new();

    protected ShortcutContext()
    {
        Services.AddSingleton(Shortcuts);
        Services.AddSingleton<TimeProvider>(PlaybackClock);
    }

    // Raised on the renderer's own thread, as the page's key listener and the menu bar do.
    protected Task Press(StepCommand command, int number = 0) => Shortcuts.RaiseAsync(new StepShortcut(command, number));
}
