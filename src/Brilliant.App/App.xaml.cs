namespace Brilliant.App;

public partial class App : Application
{
    public App() => InitializeComponent();

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "Brilliant", Width = 1000, Height = 760 };
}
