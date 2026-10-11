namespace Brilliant.Core.Content;

/// <summary>
/// How much room the app has, as far as what it offers is concerned: a phone gets single-column layouts and tap-friendly
/// forms of questions, a tablet gets two-column steps, a desktop window also gets the sidebar. Chosen from the window's width,
/// not from the device, so a narrow Mac window behaves like a phone. The default for anything that doesn't know is <see cref="Desktop"/>.
/// </summary>
public enum ScreenClass
{
    Desktop,
    Tablet,
    Phone,
}

public static class ScreenClasses
{
    /// <summary>The widest window, in CSS px, that is still a phone. Matches the stylesheet's <c>narrow</c> variant.</summary>
    public const double PhoneMaxWidth = 600;

    /// <summary>The narrowest window, in CSS px at the default 16px rem, that gets the desktop sidebar. Matches the stylesheet's <c>wide</c> variant (56rem).</summary>
    public const double DesktopMinWidth = 896;

    public static ScreenClass FromWidth(double cssPixels) =>
        cssPixels <= PhoneMaxWidth ? ScreenClass.Phone : cssPixels < DesktopMinWidth ? ScreenClass.Tablet : ScreenClass.Desktop;
}
