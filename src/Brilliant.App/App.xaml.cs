using Brilliant.Core.Apps;

namespace Brilliant.App;

public partial class App : Application
{
    private readonly AppManifest _manifest;

    public App(AppManifest manifest)
    {
        _manifest = manifest;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = _manifest.DisplayName, Width = 1000, Height = 760 };
}
