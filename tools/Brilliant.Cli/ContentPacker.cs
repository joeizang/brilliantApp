using Brilliant.Core.Content;

namespace Brilliant.Cli;

public static class ContentPacker
{
    /// <summary>Validates <paramref name="contentRoot"/> and, if valid, writes a versioned pack to <paramref name="outputPath"/>.</summary>
    public static ValidationReport Pack(string contentRoot, string outputPath)
    {
        var report = ContentValidator.Load(contentRoot, out var content);
        if (!report.IsValid || content is null) return report;

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (dir is not null) Directory.CreateDirectory(dir);

        using var stream = File.Create(outputPath);
        ContentPackFormat.Write(stream, content.Manifest, content.Tracks, content.Lessons);
        return report;
    }
}
