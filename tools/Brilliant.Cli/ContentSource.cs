using System.Text.RegularExpressions;
using Brilliant.Core.Content;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Brilliant.Cli;

public sealed record ValidationError(string File, string Message)
{
    public override string ToString() => $"{File}: {Message}";
}

public sealed class ValidationReport
{
    private readonly List<ValidationError> _errors = [];
    public IReadOnlyList<ValidationError> Errors => _errors;
    public bool IsValid => _errors.Count == 0;
    internal void Add(string file, string message) => _errors.Add(new ValidationError(file, message));
}

public sealed record LoadedContent(PackManifest Manifest, IReadOnlyList<Track> Tracks, IReadOnlyList<Lesson> Lessons);

/// <summary>
/// Reads and validates a content directory:
/// <code>
/// pack.yaml
/// tracks/&lt;track&gt;/track.yaml
/// tracks/&lt;track&gt;/&lt;lesson&gt;/lesson.yaml
/// </code>
/// Lessons are ordered by folder name within their track.
/// </summary>
public static partial class ContentValidator
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    [GeneratedRegex(@"^(track|lesson|step)\.[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionPattern();

    public static ValidationReport Validate(string contentRoot) => Load(contentRoot, out _);

    public static ValidationReport Load(string contentRoot, out LoadedContent? content)
    {
        var report = new ValidationReport();
        content = null;

        if (!Directory.Exists(contentRoot))
        {
            report.Add(contentRoot, "content directory does not exist.");
            return report;
        }

        var manifest = LoadManifest(contentRoot, report);
        var tracks = new List<Track>();
        var lessons = new List<Lesson>();
        var seenIds = new Dictionary<string, string>(StringComparer.Ordinal);

        var tracksDir = Path.Combine(contentRoot, "tracks");
        if (!Directory.Exists(tracksDir))
            report.Add(Rel(contentRoot, tracksDir), "missing 'tracks' directory.");
        else
            foreach (var trackDir in Directory.GetDirectories(tracksDir).Order(StringComparer.Ordinal))
                LoadTrack(contentRoot, trackDir, report, seenIds, tracks, lessons);

        if (report.IsValid && manifest is not null)
            content = new LoadedContent(manifest, tracks, lessons);
        return report;
    }

    private static PackManifest? LoadManifest(string root, ValidationReport report)
    {
        var path = Path.Combine(root, "pack.yaml");
        var dto = Read<PackDto>(root, path, report);
        if (dto is null) return null;

        if (string.IsNullOrWhiteSpace(dto.Id)) report.Add("pack.yaml", "'id' is required.");
        if (string.IsNullOrWhiteSpace(dto.Version) || !VersionPattern().IsMatch(dto.Version))
            report.Add("pack.yaml", "'version' is required and must look like 1.2.3.");
        return report.IsValid ? new PackManifest(dto.Id!, dto.Version!, ContentPackFormat.CurrentFormatVersion) : null;
    }

    private static void LoadTrack(string root, string trackDir, ValidationReport report,
        Dictionary<string, string> seenIds, List<Track> tracks, List<Lesson> lessons)
    {
        var trackFile = Path.Combine(trackDir, "track.yaml");
        var trackRel = Rel(root, trackFile);
        var dto = Read<TrackDto>(root, trackFile, report);
        if (dto is null) return;

        CheckId(dto.Id, "track", trackRel, report, seenIds);
        Require(dto.Title, "title", trackRel, report);

        var lessonIds = new List<string>();
        var lessonDirs = Directory.GetDirectories(trackDir).Order(StringComparer.Ordinal).ToList();
        foreach (var lessonDir in lessonDirs)
        {
            var lessonFile = Path.Combine(lessonDir, "lesson.yaml");
            var lessonRel = Rel(root, lessonFile);
            var lessonDto = Read<LessonDto>(root, lessonFile, report);
            if (lessonDto is null) continue;

            CheckId(lessonDto.Id, "lesson", lessonRel, report, seenIds);
            Require(lessonDto.Title, "title", lessonRel, report);

            var steps = new List<Step>();
            if (lessonDto.Steps is null or { Count: 0 })
                report.Add(lessonRel, "a lesson needs at least one step.");
            else
                for (var i = 0; i < lessonDto.Steps.Count; i++)
                    if (LoadStep(lessonDto.Steps[i], $"steps[{i}]", lessonRel, report, seenIds) is { } step)
                        steps.Add(step);

            if (lessonDto.Id is not null)
            {
                lessonIds.Add(lessonDto.Id);
                lessons.Add(new Lesson(lessonDto.Id, lessonDto.Title ?? "", dto.Id ?? "", steps));
            }
        }

        if (lessonDirs.Count == 0) report.Add(trackRel, "a track needs at least one lesson folder.");
        if (dto.Id is not null) tracks.Add(new Track(dto.Id, dto.Title ?? "", lessonIds));
    }

    private static Step? LoadStep(StepDto dto, string where, string file, ValidationReport report,
        Dictionary<string, string> seenIds)
    {
        var label = dto.Id is null ? where : $"{where} ('{dto.Id}')";
        var before = report.Errors.Count;

        CheckId(dto.Id, "step", file, report, seenIds, label);
        Require(dto.Title, "title", file, report, label);

        if (dto.Type != "explain")
        {
            report.Add(file, $"{label}: unsupported step type '{dto.Type ?? "(missing)"}' (supported: explain).");
            return null;
        }

        Require(dto.Body, "body", file, report, label);

        var snippets = new List<CodeSnippet>();
        for (var i = 0; i < (dto.Snippets?.Count ?? 0); i++)
        {
            var s = dto.Snippets![i];
            if (string.IsNullOrWhiteSpace(s.Language)) report.Add(file, $"{label}: snippets[{i}] needs a 'language'.");
            if (string.IsNullOrWhiteSpace(s.Code)) report.Add(file, $"{label}: snippets[{i}] needs 'code'.");
            snippets.Add(new CodeSnippet(s.Language ?? "", s.Code ?? ""));
        }

        Comparison? comparison = null;
        if (dto.Comparison is { } c)
        {
            if (string.IsNullOrWhiteSpace(c.Csharp)) report.Add(file, $"{label}: comparison needs 'csharp'.");
            if (string.IsNullOrWhiteSpace(c.Python)) report.Add(file, $"{label}: comparison needs 'python'.");
            comparison = new Comparison(c.Csharp ?? "", c.Python ?? "");
        }

        return report.Errors.Count == before
            ? new ExplainStep(dto.Id!, dto.Title!, dto.Body!, snippets, comparison)
            : null;
    }

    private static void CheckId(string? id, string kind, string file, ValidationReport report,
        Dictionary<string, string> seenIds, string? label = null)
    {
        var prefix = label is null ? "" : $"{label}: ";
        if (string.IsNullOrWhiteSpace(id))
        {
            report.Add(file, $"{prefix}'id' is required.");
            return;
        }
        if (!IdPattern().IsMatch(id) || !id.StartsWith(kind + ".", StringComparison.Ordinal))
        {
            report.Add(file, $"{prefix}id '{id}' is invalid; expected '{kind}.' followed by lowercase words joined by '-' or '.' (e.g. {kind}.hello-world).");
            return;
        }
        if (!seenIds.TryAdd(id, file))
            report.Add(file, $"{prefix}duplicate id '{id}' (already used in {seenIds[id]}).");
    }

    private static void Require(string? value, string field, string file, ValidationReport report, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            report.Add(file, $"{(label is null ? "" : label + ": ")}'{field}' is required.");
    }

    private static T? Read<T>(string root, string path, ValidationReport report) where T : class
    {
        var rel = Rel(root, path);
        if (!File.Exists(path))
        {
            report.Add(rel, "file is missing.");
            return null;
        }
        try
        {
            return Yaml.Deserialize<T>(File.ReadAllText(path))
                   ?? throw new InvalidOperationException("file is empty.");
        }
        catch (YamlException ex)
        {
            var inner = ex.InnerException?.Message ?? ex.Message;
            report.Add(rel, $"line {ex.Start.Line}: {inner}");
        }
        catch (InvalidOperationException ex)
        {
            report.Add(rel, ex.Message);
        }
        return null;
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private sealed class PackDto { public string? Id { get; set; } public string? Version { get; set; } }
    private sealed class TrackDto { public string? Id { get; set; } public string? Title { get; set; } }
    private sealed class LessonDto { public string? Id { get; set; } public string? Title { get; set; } public List<StepDto>? Steps { get; set; } }
    private sealed class SnippetDto { public string? Language { get; set; } public string? Code { get; set; } }
    private sealed class ComparisonDto { public string? Csharp { get; set; } public string? Python { get; set; } }
    private sealed class StepDto
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public List<SnippetDto>? Snippets { get; set; }
        public ComparisonDto? Comparison { get; set; }
    }
}
