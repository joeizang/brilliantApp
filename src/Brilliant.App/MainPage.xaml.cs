using Brilliant.Lessons.UI;

namespace Brilliant.App;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        AddLessonMenu();
    }

    // Mac Catalyst hands an accelerator's key straight to UIKeyCommand, which names Return and the arrows by these input strings.
    private const string Return = "\r";
    private const string LeftArrow = "UIKeyInputLeftArrow";

    // The Lesson menu puts the keyboard commands in the Mac menu bar, where they can be discovered and clicked.
    // They go onto the same bus as the page's key listener: Run never moves on, so a command that arrives twice is harmless.
    private void AddLessonMenu()
    {
        var lesson = new MenuBarItem { Text = "Lesson" };
        lesson.Add(Item("Run / Check Answer", StepCommand.Run, Return, KeyboardAcceleratorModifiers.Cmd));
        lesson.Add(Item("Continue", StepCommand.Advance, Return, KeyboardAcceleratorModifiers.Cmd | KeyboardAcceleratorModifiers.Shift));
        lesson.Add(Item("Back", StepCommand.Back, LeftArrow, KeyboardAcceleratorModifiers.Cmd | KeyboardAcceleratorModifiers.Alt));
        MenuBarItems.Add(lesson);
    }

    private static MenuFlyoutItem Item(string text, StepCommand command, string key, KeyboardAcceleratorModifiers modifiers)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = key, Modifiers = modifiers });
        item.Clicked += async (_, _) =>
        {
            var bus = IPlatformApplication.Current?.Services.GetService<StepShortcuts>();
            if (bus is not null) await bus.RaiseAsync(new StepShortcut(command));
        };
        return item;
    }
}
