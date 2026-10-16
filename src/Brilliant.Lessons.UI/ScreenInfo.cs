using Brilliant.Core.Content;
using Microsoft.JSInterop;

namespace Brilliant.Lessons.UI;

/// <summary>
/// How much room the window has right now (phone, tablet or desktop; see <see cref="ScreenClass"/>), for the parts of the app that
/// change what they offer rather than just how they are laid out: the "best on a larger screen" note, the tap-friendly form of a
/// question, the form Review queues. Layout alone is left to the stylesheet's media queries.
/// Starts as <see cref="ScreenClass.Desktop"/> until the page reports its width (screen.js), and follows the window as it is resized or turned.
/// </summary>
public sealed class ScreenInfo(bool csharpWriteCodeAvailable = true)
{
    /// <summary>Native host capability, independent of window width: C# execution is available on the Mac.</summary>
    public bool CSharpWriteCodeAvailable { get; } = csharpWriteCodeAvailable;

    public bool RequiresPhoneAlternative(Step step) => !CSharpWriteCodeAvailable && step is WriteCodeStep { Language: "csharp" };

    /// <summary>Uses a tap-friendly form on a phone or whenever the native host cannot execute this C# step.</summary>
    public Step Present(Step step) => RequiresPhoneAlternative(step) ? step.PhoneAlternative ?? step : step.For(Class);

    public ScreenClass Class { get; private set; } = ScreenClass.Desktop;

    public bool IsPhone => Class == ScreenClass.Phone;

    /// <summary>Raised when the class changes, from whatever thread noticed.</summary>
    public event Action? Changed;

    public void Set(ScreenClass screen)
    {
        if (screen == Class) return;
        Class = screen;
        Changed?.Invoke();
    }

    /// <summary>Called by screen.js when a media query flips.</summary>
    [JSInvokable]
    public void OnScreenChanged(string name) => Set(Parse(name));

    internal static ScreenClass Parse(string name) => name switch
    {
        "phone" => ScreenClass.Phone,
        "tablet" => ScreenClass.Tablet,
        _ => ScreenClass.Desktop,
    };

    /// <summary>Starts following the window. Returns what to dispose to stop. Never throws: the app is fine as a desktop layout if this can't start.</summary>
    public async Task<IAsyncDisposable?> WatchAsync(IJSRuntime js)
    {
        try
        {
            var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Brilliant.Lessons.UI/screen.js");
            var self = DotNetObjectReference.Create(this);
            Set(Parse(await module.InvokeAsync<string>("attach", self)));
            return new Watch(module, self);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    private sealed class Watch(IJSObjectReference module, DotNetObjectReference<ScreenInfo> self) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await module.InvokeVoidAsync("detach");
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException) { }   // the app is shutting down
            self.Dispose();
        }
    }
}
