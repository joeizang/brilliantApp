using Brilliant.Core.Content;

namespace Brilliant.Cli;

public static class ContentPacker
{
    /// <summary>Validates <paramref name="contentRoot"/> and, if valid, writes a versioned pack to <paramref name="outputPath"/>.</summary>
    public static ValidationReport Pack(string contentRoot, string outputPath, IReferenceSolutionRunner? runner = null, string? appManifestPath = null)
    {
        var report = ContentValidator.Load(contentRoot, out var content, runner, appManifestPath);
        if (!report.IsValid || content is null) return report;

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (dir is not null) Directory.CreateDirectory(dir);

        using var stream = File.Create(outputPath);
        ContentPackFormat.Write(stream, content.Manifest, content.Tracks, content.Lessons);
        return report;
    }

    /// <summary>Validates <paramref name="contentRoot"/> and, if valid, returns the pack's bytes (the same bytes <see cref="Pack"/> would write); null otherwise.</summary>
    public static byte[]? PackToBytes(string contentRoot, out ValidationReport report, IReferenceSolutionRunner? runner = null, string? appManifestPath = null)
    {
        report = ContentValidator.Load(contentRoot, out var content, runner, appManifestPath);
        if (!report.IsValid || content is null) return null;

        using var stream = new MemoryStream();
        ContentPackFormat.Write(stream, content.Manifest, content.Tracks, content.Lessons);
        return stream.ToArray();
    }
}
