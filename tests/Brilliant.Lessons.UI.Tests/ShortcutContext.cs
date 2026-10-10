using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>A bUnit context with the shortcut bus every step view listens on, plus a way to press a shortcut.</summary>
public abstract class ShortcutContext : BunitContext
{
    protected StepShortcuts Shortcuts { get; } = new();

    protected ShortcutContext() => Services.AddSingleton(Shortcuts);

    // Raised on the renderer's own thread, as the page's key listener and the menu bar do.
    protected Task Press(StepCommand command, int number = 0) => Shortcuts.RaiseAsync(new StepShortcut(command, number));
}
