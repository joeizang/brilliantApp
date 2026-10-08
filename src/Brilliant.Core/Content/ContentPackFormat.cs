using System.IO.Compression;
using System.Text.Json;

namespace Brilliant.Core.Content;

/// <summary>
/// A Content Pack is a zip archive containing <c>manifest.json</c> and <c>content.json</c> (normalised content).
/// </summary>
public static class ContentPackFormat
{
    public const int CurrentFormatVersion = 1;
    public const string ManifestEntry = "manifest.json";
    public const string ContentEntry = "content.json";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private sealed record ContentDocument(IReadOnlyList<Track> Tracks, IReadOnlyList<Lesson> Lessons);

    public static void Write(Stream destination, PackManifest manifest, IEnumerable<Track> tracks, IEnumerable<Lesson> lessons)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(zip, ManifestEntry, manifest);
        WriteEntry(zip, ContentEntry, new ContentDocument(tracks.ToList(), lessons.ToList()));
    }

    public static ContentGraph Load(Stream source)
    {
        try
        {
            using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            var manifest = ReadEntry<PackManifest>(zip, ManifestEntry);
            if (manifest.FormatVersion != CurrentFormatVersion)
                throw new ContentPackException(
                    $"Unsupported content pack format {manifest.FormatVersion} (expected {CurrentFormatVersion}).");
            var content = ReadEntry<ContentDocument>(zip, ContentEntry);
            return new ContentGraph(manifest, content.Tracks, content.Lessons);
        }
        catch (InvalidDataException ex)
        {
            throw new ContentPackException("Content pack is not a valid zip archive.", ex);
        }
        catch (JsonException ex)
        {
            throw new ContentPackException($"Content pack JSON is malformed: {ex.Message}", ex);
        }
    }

    public static ContentGraph Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    private static void WriteEntry<T>(ZipArchive zip, string name, T value)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        // Fixed timestamp keeps packs byte-for-byte reproducible.
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonOptions);
    }

    private static T ReadEntry<T>(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new ContentPackException($"Content pack is missing '{name}'.");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
               ?? throw new ContentPackException($"'{name}' is empty.");
    }
}
