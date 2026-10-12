using Brilliant.Lessons.UI;

namespace Brilliant.App;

public partial class MainPage : ContentPage
{
    public MainPage() => InitializeComponent();

#if ANDROID
    /// <summary>
    /// The Android back gesture is the app's own Back: one level up (lesson → track → Tracks → Today), like the Mac menu's Back. The app
    /// is one page whose screens the course shell swaps itself, so without this Android sees nothing to go back to and closes it. Only on
    /// Today, the top, does back fall through and leave the app.
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        var shortcuts = Handler?.MauiContext?.Services.GetService<StepShortcuts>();
        if (shortcuts is not { CanGoBack: true }) return base.OnBackButtonPressed();
        _ = shortcuts.RaiseAsync(new StepShortcut(StepCommand.Back));
        return true;
    }
#endif
}
