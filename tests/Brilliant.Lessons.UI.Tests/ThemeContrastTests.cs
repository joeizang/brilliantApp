using System.Globalization;
using System.Text.RegularExpressions;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>
/// The colours text is drawn in must be readable on the surfaces it sits on, in both themes (WCAG AA, 4.5:1 for small text).
/// <c>--ember</c> is the flame's colour and is too light to carry words on a light background; <c>--ember-text</c> is the one for
/// labels and for the fill of a badge with text on it.
/// </summary>
public partial class ThemeContrastTests
{
    private const double SmallTextMinimum = 4.5;

    [GeneratedRegex(@"--(?<name>[a-z-]+):\s*(?<hex>#[0-9a-fA-F]{6})\b")]
    private static partial Regex Token();

    /// <summary>The colour tokens of the dark (default) theme and of the light one, from the stylesheet the app ships.</summary>
    private static (Dictionary<string, string> Dark, Dictionary<string, string> Light) Themes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BrilliantApp.sln"))) dir = dir.Parent;
        var css = File.ReadAllText(Path.Combine(dir?.FullName ?? throw new FileNotFoundException("repo root"), "src/Brilliant.Lessons.UI/wwwroot/theme.css"));

        var light = css.IndexOf("@media (prefers-color-scheme: light)", StringComparison.Ordinal);
        Assert.True(light > 0, "theme.css has no light theme");
        return (Read(css[..light]), Read(css[light..]));

        static Dictionary<string, string> Read(string block) =>
            Token().Matches(block).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["hex"].Value, StringComparer.Ordinal);
    }

    private static double Luminance(string hex)
    {
        double Channel(int at)
        {
            var c = int.Parse(hex.AsSpan(1 + at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    private static double Contrast(string a, string b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void Ember_text_is_readable_on_the_page_and_on_a_panel(string theme)
    {
        var t = theme == "dark" ? Themes().Dark : Themes().Light;
        Assert.True(t.TryGetValue("ember-text", out var ember), $"the {theme} theme has no --ember-text");

        Assert.True(Contrast(ember!, t["bg"]) >= SmallTextMinimum, $"{theme}: ember text on the page is {Contrast(ember!, t["bg"]):0.00}:1");
        Assert.True(Contrast(ember!, t["surface-raised"]) >= SmallTextMinimum, $"{theme}: ember text on a panel is {Contrast(ember!, t["surface-raised"]):0.00}:1");
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void The_due_badge_text_is_readable_on_its_ember_fill(string theme)
    {
        var t = theme == "dark" ? Themes().Dark : Themes().Light;
        Assert.True(t.TryGetValue("ember-text", out var ember), $"the {theme} theme has no --ember-text");

        Assert.True(Contrast(t["on-accent"], ember!) >= SmallTextMinimum, $"{theme}: badge text is {Contrast(t["on-accent"], ember!):0.00}:1");
    }
}
