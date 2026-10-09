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

    [GeneratedRegex(@"^(track|lesson|step|concept|review)\.[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)*$")]
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
                {
                    if (IsEmptyEntry(lessonDto.Steps[i], $"steps[{i}]", lessonRel, report)) continue;
                    if (LoadStep(lessonDto.Steps[i], $"steps[{i}]", lessonRel, report, seenIds) is { } step)
                        steps.Add(step);
                }

            var concepts = LoadConcepts(lessonDto, lessonRel, report, seenIds);
            var reviewItems = LoadReviewItems(lessonDto, lessonRel, report, seenIds, concepts, steps);

            if (lessonDto.Id is not null)
            {
                lessonIds.Add(lessonDto.Id);
                lessons.Add(new Lesson(lessonDto.Id, lessonDto.Title ?? "", dto.Id ?? "", steps)
                {
                    Concepts = concepts,
                    ReviewItems = reviewItems,
                });
            }
        }

        if (lessonDirs.Count == 0) report.Add(trackRel, "a track needs at least one lesson folder.");
        if (dto.Id is not null) tracks.Add(new Track(dto.Id, dto.Title ?? "", lessonIds));
    }

    private static List<Concept> LoadConcepts(LessonDto lesson, string file, ValidationReport report,
        Dictionary<string, string> seenIds)
    {
        var concepts = new List<Concept>();
        for (var i = 0; i < (lesson.Concepts?.Count ?? 0); i++)
        {
            var c = lesson.Concepts![i];
            if (IsEmptyEntry(c, $"concepts[{i}]", file, report)) continue;
            var label = c.Id is null ? $"concepts[{i}]" : $"concepts[{i}] ('{c.Id}')";
            CheckId(c.Id, "concept", file, report, seenIds, label);
            Require(c.Title, "title", file, report, label);
            if (c.Id is not null) concepts.Add(new Concept(c.Id, c.Title ?? ""));
        }
        return concepts;
    }

    private static List<ReviewItem> LoadReviewItems(LessonDto lesson, string file, ValidationReport report,
        Dictionary<string, string> seenIds, List<Concept> concepts, List<Step> steps)
    {
        var items = new List<ReviewItem>();
        for (var i = 0; i < (lesson.ReviewItems?.Count ?? 0); i++)
        {
            var r = lesson.ReviewItems![i];
            if (IsEmptyEntry(r, $"reviewItems[{i}]", file, report)) continue;
            var label = r.Id is null ? $"reviewItems[{i}]" : $"reviewItems[{i}] ('{r.Id}')";
            CheckId(r.Id, "review", file, report, seenIds, label);
            Require(r.Concept, "concept", file, report, label);
            Require(r.Step, "step", file, report, label);

            if (!string.IsNullOrWhiteSpace(r.Concept) && concepts.All(c => c.Id != r.Concept))
                report.Add(file, $"{label}: concept '{r.Concept}' is not declared in this lesson's 'concepts'.");
            if (!string.IsNullOrWhiteSpace(r.Step))
            {
                var step = steps.FirstOrDefault(s => s.Id == r.Step);
                if (step is null)
                    report.Add(file, $"{label}: step '{r.Step}' is not a valid step of this lesson.");
                else if (step is ExplainStep)
                    report.Add(file, $"{label}: step '{r.Step}' is an explain step; a review item needs a question (choice or predict-output).");
            }
            if (r.Id is not null && r.Concept is not null && r.Step is not null)
                items.Add(new ReviewItem(r.Id, r.Concept, r.Step));
        }
        return items;
    }

    private static List<string> LoadHints(StepDto dto, string label, string file, ValidationReport report)
    {
        var hints = dto.Hints ?? [];
        if (hints.Count == 0) return [];
        if (dto.Type is not ("choice" or "predict-output" or "write-code"))
            report.Add(file, $"{label}: 'hints' only apply to questions (choice, predict-output or write-code).");
        if (hints.Count > MaxHints)
            report.Add(file, $"{label}: at most {MaxHints} hints (nudge, pattern hint, partial, full walkthrough); found {hints.Count}.");
        for (var i = 0; i < hints.Count; i++)
            if (string.IsNullOrWhiteSpace(hints[i])) report.Add(file, $"{label}: hints[{i}] is empty.");
        return hints.Select(h => h ?? "").ToList();
    }

    private const int MaxHints = 4;

    private static Step? LoadStep(StepDto dto, string where, string file, ValidationReport report,
        Dictionary<string, string> seenIds)
    {
        var label = dto.Id is null ? where : $"{where} ('{dto.Id}')";
        var before = report.Errors.Count;

        CheckId(dto.Id, "step", file, report, seenIds, label);
        Require(dto.Title, "title", file, report, label);

        var hints = LoadHints(dto, label, file, report);
        if (dto.Type == "choice") return WithHints(LoadChoice(dto, label, file, report, before), hints);
        if (dto.Type == "predict-output") return WithHints(LoadPredictOutput(dto, label, file, report, before), hints);
        if (dto.Type == "write-code") return WithHints(LoadWriteCode(dto, label, file, report, before), hints);

        if (dto.Type != "explain")
        {
            report.Add(file, $"{label}: unsupported step type '{dto.Type ?? "(missing)"}' (supported: explain, choice, predict-output, write-code).");
            return null;
        }

        Require(dto.Body, "body", file, report, label);

        var snippets = new List<CodeSnippet>();
        for (var i = 0; i < (dto.Snippets?.Count ?? 0); i++)
        {
            var s = dto.Snippets![i];
            if (IsEmptyEntry(s, $"{label}: snippets[{i}]", file, report)) continue;
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

    /// <summary>A bare `-` or `[null]` in a YAML list deserializes to null; report it instead of crashing.</summary>
    private static bool IsEmptyEntry(object? entry, string where, string file, ValidationReport report)
    {
        if (entry is not null) return false;
        report.Add(file, $"{where}: entry is empty; remove the stray '-' or fill it in.");
        return true;
    }

    private static Step? WithHints(Step? step, List<string> hints) =>
        step is null || hints.Count == 0 ? step : step with { Hints = hints };

    private static Step? LoadChoice(StepDto dto, string label, string file, ValidationReport report, int errorsBefore)
    {
        Require(dto.Prompt, "prompt", file, report, label);

        var options = new List<ChoiceOption>();
        for (var i = 0; i < (dto.Options?.Count ?? 0); i++)
        {
            var o = dto.Options![i];
            if (IsEmptyEntry(o, $"{label}: options[{i}]", file, report)) continue;
            if (string.IsNullOrWhiteSpace(o.Text)) report.Add(file, $"{label}: options[{i}] needs 'text'.");
            options.Add(new ChoiceOption(o.Text ?? "", o.Correct ?? false, o.Feedback));
        }

        var multi = dto.MultiSelect ?? false;
        var correct = options.Count(o => o.Correct);
        if (options.Count < 2)
            report.Add(file, $"{label}: a choice step needs at least 2 options.");
        else if (correct == 0)
            report.Add(file, $"{label}: no option is marked 'correct: true'.");
        else if (!multi && correct != 1)
            report.Add(file, $"{label}: {correct} options are correct but 'multiSelect' is not true; mark exactly one correct option or set multiSelect: true.");

        return report.Errors.Count == errorsBefore
            ? new ChoiceStep(dto.Id!, dto.Title!, dto.Prompt!, multi, options)
            : null;
    }

    private static Step? LoadPredictOutput(StepDto dto, string label, string file, ValidationReport report, int errorsBefore)
    {
        Require(dto.Prompt, "prompt", file, report, label);
        Require(dto.Code, "code", file, report, label);

        var hasOptions = dto.Options is { Count: > 0 };
        var hasAccepted = dto.Accepted is { Count: > 0 };
        var options = new List<ChoiceOption>();
        var accepted = new List<string>();
        var mistakes = new List<MistakePattern>();

        if (hasOptions == hasAccepted)
            report.Add(file, $"{label}: give either 'accepted' answers (typed) or 'options' (multiple choice), not {(hasOptions ? "both" : "neither")}.");
        else if (hasOptions)
        {
            if (dto.Mistakes is { Count: > 0 })
                report.Add(file, $"{label}: 'mistakes' only apply to typed steps; use per-option 'feedback' instead.");
            for (var i = 0; i < dto.Options!.Count; i++)
            {
                var o = dto.Options[i];
                if (IsEmptyEntry(o, $"{label}: options[{i}]", file, report)) continue;
                if (string.IsNullOrWhiteSpace(o.Text)) report.Add(file, $"{label}: options[{i}] needs 'text'.");
                options.Add(new ChoiceOption(o.Text ?? "", o.Correct ?? false, o.Feedback));
            }
            if (options.Count < 2)
                report.Add(file, $"{label}: a multiple-choice prediction needs at least 2 options.");
            else if (options.Count(o => o.Correct) != 1)
                report.Add(file, $"{label}: exactly one option must be marked 'correct: true'.");
        }
        else
        {
            for (var i = 0; i < dto.Accepted!.Count; i++)
            {
                if (AnswerEvaluator.Normalize(dto.Accepted[i]).Length == 0)
                    report.Add(file, $"{label}: accepted[{i}] is empty.");
                accepted.Add(dto.Accepted[i] ?? "");
            }

            var acceptedNormalised = accepted.Select(AnswerEvaluator.Normalize).ToHashSet();
            for (var i = 0; i < (dto.Mistakes?.Count ?? 0); i++)
            {
                var m = dto.Mistakes![i];
                var where = $"{label}: mistakes[{i}]";
                if (IsEmptyEntry(m, where, file, report)) continue;
                var answers = m.Answers ?? [];
                if (answers.Count == 0 && string.IsNullOrWhiteSpace(m.Regex))
                    report.Add(file, $"{where} needs 'answers' and/or 'regex'.");
                if (string.IsNullOrWhiteSpace(m.Feedback))
                    report.Add(file, $"{where} needs 'feedback'.");
                foreach (var a in answers.Where(a => acceptedNormalised.Contains(AnswerEvaluator.Normalize(a))))
                    report.Add(file, $"{where}: answer '{a}' is also an accepted answer.");
                if (!string.IsNullOrWhiteSpace(m.Regex))
                {
                    try { _ = new Regex(m.Regex); }
                    catch (ArgumentException ex) { report.Add(file, $"{where}: invalid regex ({ex.Message})"); }
                }
                mistakes.Add(new MistakePattern(answers, string.IsNullOrWhiteSpace(m.Regex) ? null : m.Regex, m.Feedback ?? ""));
            }
        }

        return report.Errors.Count == errorsBefore
            ? new PredictOutputStep(dto.Id!, dto.Title!, dto.Prompt!, new CodeSnippet(dto.Language ?? "python", dto.Code!), accepted, mistakes, options)
            : null;
    }

    private static readonly Regex PythonIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static Step? LoadWriteCode(StepDto dto, string label, string file, ValidationReport report, int errorsBefore)
    {
        Require(dto.Prompt, "prompt", file, report, label);
        Require(dto.Entrypoint, "entrypoint", file, report, label);
        if (dto.Language is null) report.Add(file, $"{label}: 'language' is required (supported: python).");
        else if (dto.Language != "python") report.Add(file, $"{label}: unsupported language '{dto.Language}' (supported: python).");
        if (!string.IsNullOrWhiteSpace(dto.Entrypoint) && !PythonIdentifier.IsMatch(dto.Entrypoint))
            report.Add(file, $"{label}: entrypoint '{dto.Entrypoint}' is not a valid Python function name.");

        var tests = new List<CodeTest>();
        if (dto.Tests is null or { Count: 0 })
            report.Add(file, $"{label}: a write-code step needs at least one hidden test.");
        else
            for (var i = 0; i < dto.Tests.Count; i++)
            {
                var t = dto.Tests[i];
                if (IsEmptyEntry(t, $"{label}: tests[{i}]", file, report)) continue;
                // 'input' may be empty (a function with no arguments); 'expected' never is.
                if (t.Input is null) report.Add(file, $"{label}: tests[{i}] needs 'input' (the call's arguments as Python source; \"\" for none).");
                if (string.IsNullOrWhiteSpace(t.Expected)) report.Add(file, $"{label}: tests[{i}] needs 'expected' (a Python expression).");
                tests.Add(new CodeTest(t.Input ?? "", t.Expected ?? ""));
            }

        return report.Errors.Count == errorsBefore
            ? new WriteCodeStep(dto.Id!, dto.Title!, dto.Prompt!, dto.Language!, dto.Starter ?? "", dto.Entrypoint!, tests)
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
    private sealed class LessonDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public List<StepDto>? Steps { get; set; }
        public List<ConceptDto>? Concepts { get; set; }
        public List<ReviewItemDto>? ReviewItems { get; set; }
    }
    private sealed class ConceptDto { public string? Id { get; set; } public string? Title { get; set; } }
    private sealed class ReviewItemDto { public string? Id { get; set; } public string? Concept { get; set; } public string? Step { get; set; } }
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
        public string? Prompt { get; set; }
        public bool? MultiSelect { get; set; }
        public List<OptionDto>? Options { get; set; }
        public string? Code { get; set; }
        public string? Language { get; set; }
        public List<string>? Accepted { get; set; }
        public List<MistakeDto>? Mistakes { get; set; }
        public List<string>? Hints { get; set; }
        public string? Starter { get; set; }
        public string? Entrypoint { get; set; }
        public List<TestDto>? Tests { get; set; }
    }
    private sealed class TestDto { public string? Input { get; set; } public string? Expected { get; set; } }
    private sealed class MistakeDto { public List<string>? Answers { get; set; } public string? Regex { get; set; } public string? Feedback { get; set; } }
    private sealed class OptionDto { public string? Text { get; set; } public bool? Correct { get; set; } public string? Feedback { get; set; } }
}
