using System.IO.Compression;
using System.Text.Json;
using Brilliant.Core.Content;

namespace Brilliant.Core.Tests;

public class PackLanguagePinTests
{
    [Fact]
    public void Csharp_authoring_pins_survive_a_pack_round_trip()
    {
        var manifest = new PackManifest("pack.csharp", "1.0.0", ContentPackFormat.CurrentFormatVersion)
        {
            TargetFramework = "net10.0",
            LangVersion = "14",
        };
        using var pack = new MemoryStream();
        ContentPackFormat.Write(pack, manifest, [], []);
        pack.Position = 0;

        Assert.Equal(manifest, ContentPackFormat.Load(pack).Manifest);
    }

    [Fact]
    public void Absent_pins_leave_the_existing_Dsa_manifest_bytes_unchanged()
    {
        var manifest = new PackManifest("pack.dsa", "1.0.0", ContentPackFormat.CurrentFormatVersion);
        var previousShape = new { manifest.PackId, manifest.Version, manifest.FormatVersion };

        Assert.Equal(JsonSerializer.Serialize(previousShape, ContentPackFormat.JsonOptions),
            JsonSerializer.Serialize(manifest, ContentPackFormat.JsonOptions));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Cached_packs_without_authoring_pins_remain_readable(int format)
    {
        using var pack = new MemoryStream();
        using (var zip = new ZipArchive(pack, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var manifest = new StreamWriter(zip.CreateEntry(ContentPackFormat.ManifestEntry).Open()))
                manifest.Write($$"""{"packId":"pack.legacy","version":"1.0.0","formatVersion":{{format}}}""");
            using (var content = new StreamWriter(zip.CreateEntry(ContentPackFormat.ContentEntry).Open()))
                content.Write("""{"tracks":[],"lessons":[]}""");
        }
        pack.Position = 0;

        var loaded = ContentPackFormat.Load(pack).Manifest;

        Assert.Equal(format, loaded.FormatVersion);
        Assert.Null(loaded.TargetFramework);
        Assert.Null(loaded.LangVersion);
    }
}
