using Brilliant.Lessons.UI;
using Foundation;
using UIKit;

namespace Brilliant.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    // UIKit's key equivalents name Return and the arrows by these input strings.
    private const string Return = "\r";
    private const string LeftArrow = "UIKeyInputLeftArrow";

    // MAUI's CanPerform only answers yes for selectors starting with "MenuItem", so ours must too.
    private const string CommandSelector = "MenuItemLessonCommand:";

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    // The Lesson menu puts the keyboard commands in the Mac menu bar, where they can be discovered and clicked. MAUI rebuilds the whole
    // menu through this method (at launch and whenever it asks for a rebuild), and a menu can only be added to the builder while it runs,
    // so it is added here rather than through the page's MenuBarItems. The commands go onto the same StepShortcuts bus as the page's
    // key listener: Run never moves on, so it is harmless if both deliver it; Continue and Back are menu-only.
    [System.Runtime.Versioning.SupportedOSPlatform("ios13.0")]
    public override void BuildMenu(IUIMenuBuilder builder)
    {
        base.BuildMenu(builder);
        if (builder.System != UIMenuSystem.MainSystem) return;

        var selector = new ObjCRuntime.Selector(CommandSelector);
        var lesson = UIMenu.Create("Lesson", new UIMenuElement[]
        {
            Item("Run / Check Answer", StepCommand.Run, Return, UIKeyModifierFlags.Command, selector),
            Item("Continue", StepCommand.Advance, Return, UIKeyModifierFlags.Command | UIKeyModifierFlags.Shift, selector),
            Item("Back", StepCommand.Back, LeftArrow, UIKeyModifierFlags.Command | UIKeyModifierFlags.Alternate, selector),
        });
        builder.InsertSiblingMenuAfter(lesson, UIMenuIdentifier.View.GetConstant()!);
    }

    private static UIKeyCommand Item(string title, StepCommand command, string key, UIKeyModifierFlags modifiers, ObjCRuntime.Selector selector) =>
        UIKeyCommand.Create(title, null, selector, key, modifiers, new NSString(command.ToString()));

    [Export(CommandSelector)]
    public void LessonCommand(UICommand sender)
    {
        if (sender.PropertyList is not NSString name || !Enum.TryParse<StepCommand>(name.ToString(), out var command)) return;
        var bus = IPlatformApplication.Current?.Services.GetService<StepShortcuts>();
        if (bus is not null) _ = bus.RaiseAsync(new StepShortcut(command));
    }
}
