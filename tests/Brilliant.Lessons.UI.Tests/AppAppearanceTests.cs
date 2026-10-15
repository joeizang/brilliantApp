using Brilliant.Core.Apps;
using Bunit;

namespace Brilliant.Lessons.UI.Tests;

public class AppAppearanceTests : BunitContext
{
    [Fact]
    public void Manifest_supplies_both_system_palettes_and_the_page_title()
    {
        var app = AppManifest.Parse("""
            appId: example.app
            displayName: Example app
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: example.pack.zip
            runtimes: []
            stepTypes: [explain]
            """);

        JSInterop.Mode = JSRuntimeMode.Loose;
        var head = Render<Microsoft.AspNetCore.Components.Web.HeadOutlet>();
        var cut = Render<AppAppearance>(p => p.Add(c => c.Manifest, app));

        Assert.Equal("Example app", head.Find("title").TextContent);
        var css = cut.Find("style").TextContent;
        Assert.Contains("--accent: #112233", css);
        Assert.Contains("--on-accent: #ffffff", css);
        Assert.Contains("@media (prefers-color-scheme: light)", css);
        Assert.Contains("--accent: #445566", css);
        Assert.Contains("--on-accent: #000000", css);
    }
}
