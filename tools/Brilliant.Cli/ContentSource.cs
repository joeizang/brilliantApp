using Brilliant.Core.Code;
using System.Text.RegularExpressions;
using Brilliant.Core.Apps;
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
    private readonly List<ValidationError> _warnings = [];
    public IReadOnlyList<ValidationError> Errors => _errors;

    /// <summary>Things worth a look that don't stop the content shipping, such as a prompt that names its own pattern.</summary>
    public IReadOnlyList<ValidationError> Warnings => _warnings;
    public bool IsValid => _errors.Count == 0;
    internal void Add(string file, string message) => _errors.Add(new ValidationError(file, message));
    internal void Warn(string file, string message) => _warnings.Add(new ValidationError(file, message));
    internal List<ReferenceCheck> ReferenceChecks { get; } = [];
    internal List<TraceCheck> TraceChecks { get; } = [];

    /// <summary>What running each checked C# example recorded, by the step it belongs to and whether it is that step's walkthrough. The pack carries these.</summary>
    internal Dictionary<(string StepId, bool IsWalkthrough), TraceResult> Recordings { get; } = [];

    /// <summary>C# <c>lowered</c> steps waiting to be compiled, and what the compiler made of each, by step id. The pack carries these.</summary>
    internal List<LoweringCheck> LoweringChecks { get; } = [];
    internal Dictionary<string, LoweredCode> Lowerings { get; } = [];

    /// <summary>C# <c>benchmark-predict</c> steps waiting to be measured, and what BenchmarkDotNet recorded for each, by step id. The pack carries these.</summary>
    internal List<BenchmarkCheck> BenchmarkChecks { get; } = [];
    internal Dictionary<string, BenchmarkRecording> Benchmarks { get; } = [];

    /// <summary><c>allocations</c> predict steps waiting to be measured, and the answer options (right one marked) the measurement settled, by step id.</summary>
    internal List<AllocationCheck> AllocationChecks { get; } = [];
    internal Dictionary<string, IReadOnlyList<ChoiceOption>> AllocationOptions { get; } = [];
    internal AppManifest? App { get; set; }
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

    [GeneratedRegex(@"^(track|lesson|step|concept|review|pattern)\.[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex CandidateIdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionPattern();

    public static ValidationReport Validate(string contentRoot, IReferenceSolutionRunner? runner = null, string? appManifestPath = null) =>
        Load(contentRoot, out _, runner, appManifestPath, recordBenchmarks: false);

    /// <param name="runner">Runs write-code reference solutions; defaults to the machine's CPython.</param>
    /// <param name="recordBenchmarks">
    /// Runs each <c>benchmark-predict</c> step's BenchmarkDotNet comparison and keeps the numbers. Measuring takes seconds per step and the
    /// numbers differ on every run, so a pack build does it and a validation only checks that the candidates compile.
    /// </param>
    public static ValidationReport Load(string contentRoot, out LoadedContent? content, IReferenceSolutionRunner? runner = null, string? appManifestPath = null,
        bool recordBenchmarks = true)
    {
        var report = new ValidationReport();
        content = null;

        if (!Directory.Exists(contentRoot))
        {
            report.Add(contentRoot, "content directory does not exist.");
            return report;
        }

        try
        {
            if (appManifestPath is null) report.App = AppContentSource.Infer(contentRoot);
            else
            {
                var source = AppContentSource.FromManifest(appManifestPath);
                if (!Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar).Equals(source.Root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
                {
                    report.Add(contentRoot, $"app manifest '{appManifestPath}' selects content directory '{source.Root}', not this directory.");
                    return report;
                }
                report.App = source.App;
            }
        }
        catch (Exception ex) when (ex is AppManifestException or IOException or UnauthorizedAccessException)
        {
            report.Add(contentRoot, $"cannot load app manifest: {ex.Message}");
            return report;
        }

        var manifest = LoadManifest(contentRoot, report);
        var tracks = new List<Track>();
        var lessons = new List<Lesson>();
        var seenIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var patterns = new PatternTags();

        var tracksDir = Path.Combine(contentRoot, "tracks");
        if (!Directory.Exists(tracksDir))
            report.Add(Rel(contentRoot, tracksDir), "missing 'tracks' directory.");
        else
            foreach (var trackDir in Directory.GetDirectories(tracksDir).Order(StringComparer.Ordinal))
                LoadTrack(contentRoot, trackDir, report, seenIds, tracks, lessons, patterns);
        patterns.Check(report);

        if ((report.ReferenceChecks.Any(c => c.Language == "csharp") || report.LoweringChecks.Count > 0 || report.BenchmarkChecks.Count > 0 || report.AllocationChecks.Count > 0) &&
            (manifest?.TargetFramework != CSharpPins.TargetFramework || manifest.LangVersion != CSharpPins.LanguageVersion))
            report.Add("pack.yaml", $"C# reference solutions, benchmark steps and lowered steps require targetFramework: {CSharpPins.TargetFramework} and langVersion: {CSharpPins.LanguageVersion}.");

        // Only once the content is structurally sound: compile each lowered step's snippet and keep what the compiler made of it.
        if (report.IsValid)
            foreach (var check in report.LoweringChecks)
            {
                var (lowered, error) = CSharpLowering.Lower(check.Code);
                if (lowered is null) report.Add(check.File, $"{check.Label}: {error}");
                else report.Lowerings[check.StepId] = lowered;
            }

        // Only once the content is structurally sound: compile each benchmark step's candidates, and when building a pack, measure them.
        if (report.IsValid)
            foreach (var check in report.BenchmarkChecks)
            {
                var problem = CSharpBenchmark.Check(check.Setup, check.Candidates);
                if (problem is null && recordBenchmarks)
                {
                    var (recording, error) = CSharpBenchmark.Record(check.Measure, check.Setup, check.Candidates);
                    if (recording is not null) report.Benchmarks[check.StepId] = recording;
                    problem = error;
                }
                if (problem is not null) report.Add(check.File, $"{check.Label}: {problem}");
            }

        // Only once the content is structurally sound: prove every exercise's reference solution passes its own tests.
        if (report.IsValid && (report.ReferenceChecks.Count > 0 || report.TraceChecks.Count > 0 || report.AllocationChecks.Count > 0))
            ReferenceSolutionChecker.CheckAll(report, runner);

        if (report.IsValid && manifest is not null)
            content = new LoadedContent(manifest, tracks, report.Recordings.Count == 0 && report.Lowerings.Count == 0 && report.Benchmarks.Count == 0 && report.AllocationOptions.Count == 0
                ? lessons : lessons.Select(l => WithRecordings(l, report.Recordings, report.Lowerings, report.Benchmarks, report.AllocationOptions)).ToList());
        return report;
    }

    /// <summary>The lesson with each recorded example attached to the step (or walkthrough) it was run for.</summary>
    private static Lesson WithRecordings(Lesson lesson, IReadOnlyDictionary<(string StepId, bool IsWalkthrough), TraceResult> recordings,
        IReadOnlyDictionary<string, LoweredCode> lowerings, IReadOnlyDictionary<string, BenchmarkRecording> benchmarks,
        IReadOnlyDictionary<string, IReadOnlyList<ChoiceOption>> allocations)
    {
        Step Attach(Step step)
        {
            if (step is LoweredStep lowered && lowerings.TryGetValue(step.Id, out var made)) step = lowered with { Lowered = made };
            if (step is BenchmarkPredictStep benchmark && benchmarks.TryGetValue(step.Id, out var measured)) step = benchmark with { Recorded = measured };
            if (step is PredictOutputStep predicted && allocations.TryGetValue(step.Id, out var answers)) step = predicted with { Options = answers };
            if (step is TraceStep trace && recordings.TryGetValue((step.Id, false), out var recorded)) step = trace with { Recorded = recorded };
            if (step.Walkthrough is { } walkthrough && recordings.TryGetValue((step.Id, true), out var walked))
                step = step with { Walkthrough = walkthrough with { Recorded = walked } };
            if (step.PhoneAlternative is { } alternative) step = step with { PhoneAlternative = Attach(alternative) };
            return step;
        }
        return lesson with { Steps = lesson.Steps.Select(Attach).ToList(), Practice = lesson.Practice.Select(Attach).ToList() };
    }

    private static PackManifest? LoadManifest(string root, ValidationReport report)
    {
        var path = Path.Combine(root, "pack.yaml");
        var dto = Read<PackDto>(root, path, report);
        if (dto is null) return null;

        if (string.IsNullOrWhiteSpace(dto.Id)) report.Add("pack.yaml", "'id' is required.");
        if (string.IsNullOrWhiteSpace(dto.Version) || !VersionPattern().IsMatch(dto.Version))
            report.Add("pack.yaml", "'version' is required and must look like 1.2.3.");
        var csharp = report.App?.SupportsRuntime("csharp") == true;
        if (dto.TargetFramework is null)
        {
            if (csharp) report.Add("pack.yaml", $"'targetFramework' is required for apps enabling csharp (supported: {CSharpPins.TargetFramework}).");
        }
        else if (dto.TargetFramework != CSharpPins.TargetFramework)
            report.Add("pack.yaml", $"unsupported targetFramework '{dto.TargetFramework}' (supported: {CSharpPins.TargetFramework}).");
        if (dto.LangVersion is null)
        {
            if (csharp) report.Add("pack.yaml", $"'langVersion' is required for apps enabling csharp (supported: {CSharpPins.LanguageVersion}).");
        }
        else if (dto.LangVersion != CSharpPins.LanguageVersion)
            report.Add("pack.yaml", $"unsupported langVersion '{dto.LangVersion}' (supported: {CSharpPins.LanguageVersion}).");

        return report.IsValid ? new PackManifest(dto.Id!, dto.Version!, ContentPackFormat.CurrentFormatVersion)
        {
            TargetFramework = dto.TargetFramework,
            LangVersion = dto.LangVersion,
        } : null;
    }

    private static void LoadTrack(string root, string trackDir, ValidationReport report,
        Dictionary<string, string> seenIds, List<Track> tracks, List<Lesson> lessons, PatternTags patternTags)
    {
        var trackFile = Path.Combine(trackDir, "track.yaml");
        var trackRel = Rel(root, trackFile);
        var dto = Read<TrackDto>(root, trackFile, report);
        if (dto is null) return;

        CheckId(dto.Id, "track", trackRel, report, seenIds);
        Require(dto.Title, "title", trackRel, report);

        var lessonIds = new List<string>();
        var lessonDirs = Directory.GetDirectories(trackDir).Order(StringComparer.Ordinal).ToList();
        for (var position = 0; position < lessonDirs.Count; position++)
        {
            var lessonDir = lessonDirs[position];
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
                    {
                        steps.Add(step);
                        if (step.Pattern is not null)
                            patternTags.Tag(new PatternTag(lessonRel, $"steps[{i}] ('{step.Id}')", step, trackDir, position));
                    }
                }

            var practice = LoadPractice(root, lessonDir, trackDir, position, report, seenIds, patternTags);
            var concepts = LoadConcepts(lessonDto, lessonRel, report, seenIds);
            var reviewItems = LoadReviewItems(lessonDto, lessonRel, report, seenIds, concepts, steps);
            var patterns = LoadPatterns(lessonDto, lessonRel, report, seenIds);
            foreach (var pattern in patterns)
                patternTags.Declare(pattern, lessonDto.Id ?? lessonRel, trackDir, position);

            if (lessonDto.Id is not null)
            {
                lessonIds.Add(lessonDto.Id);
                lessons.Add(new Lesson(lessonDto.Id, lessonDto.Title ?? "", dto.Id ?? "", steps)
                {
                    Concepts = concepts,
                    ReviewItems = reviewItems,
                    Patterns = patterns,
                    Practice = practice,
                });
            }
        }

        if (lessonDirs.Count == 0) report.Add(trackRel, "a track needs at least one lesson folder.");
        if (dto.Id is not null) tracks.Add(new Track(dto.Id, dto.Title ?? "", lessonIds));
    }

    /// <summary>
    /// A lesson's optional <c>practice.yaml</c>: problems that exist only for Practice and Interview. Each is a write-code, fill-blank or
    /// parsons step with a <c>pattern</c> (and so a <c>patternCue</c>), checked like a lesson step: its ID is unique, its reference
    /// solution passes its tests, and its pattern is taught by this lesson or an earlier one.
    /// </summary>
    private static List<Step> LoadPractice(string root, string lessonDir, string trackDir, int position, ValidationReport report,
        Dictionary<string, string> seenIds, PatternTags patternTags)
    {
        var problems = new List<Step>();
        var path = Path.Combine(lessonDir, "practice.yaml");
        if (!File.Exists(path)) return problems;
        var file = Rel(root, path);
        var dto = Read<PracticeDto>(root, path, report);
        if (dto is null) return problems;
        if (dto.Problems is null or { Count: 0 })
        {
            report.Add(file, "'problems' is empty; add a problem or delete the file.");
            return problems;
        }

        for (var i = 0; i < dto.Problems.Count; i++)
        {
            var where = $"problems[{i}]";
            if (IsEmptyEntry(dto.Problems[i], where, file, report)) continue;
            var problem = dto.Problems[i];
            var label = problem.Id is null ? where : $"{where} ('{problem.Id}')";
            if (problem.Type is not ("write-code" or "fill-blank" or "parsons"))
                report.Add(file, $"{label}: a practice problem is one the learner solves: write-code, fill-blank or parsons (found '{problem.Type ?? "(missing)"}').");
            else if (problem.Pattern is null)
                report.Add(file, $"{label}: a practice problem needs a 'pattern' (and a 'patternCue'); Practice only draws problems by pattern.");
            if (LoadStep(problem, where, file, report, seenIds) is not { } step) continue;
            problems.Add(step);
            if (step.Pattern is not null)
                patternTags.Tag(new PatternTag(file, label, step, trackDir, position));
        }
        return problems;
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

    private static List<Pattern> LoadPatterns(LessonDto lesson, string file, ValidationReport report,
        Dictionary<string, string> seenIds)
    {
        var patterns = new List<Pattern>();
        for (var i = 0; i < (lesson.Patterns?.Count ?? 0); i++)
        {
            var p = lesson.Patterns![i];
            if (IsEmptyEntry(p, $"patterns[{i}]", file, report)) continue;
            var label = p.Id is null ? $"patterns[{i}]" : $"patterns[{i}] ('{p.Id}')";
            CheckId(p.Id, "pattern", file, report, seenIds, label);
            Require(p.Title, "title", file, report, label);
            if (p.Id is not null) patterns.Add(new Pattern(p.Id, p.Title ?? ""));
        }
        return patterns;
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

            var kind = ReviewKind.Concept;
            if (r.Kind is not null && !TryParseKind(r.Kind, out kind))
                report.Add(file, $"{label}: kind '{r.Kind}' is not one of: concept, pattern.");

            if (!string.IsNullOrWhiteSpace(r.Concept) && concepts.All(c => c.Id != r.Concept))
                report.Add(file, $"{label}: concept '{r.Concept}' is not declared in this lesson's 'concepts'.");
            if (!string.IsNullOrWhiteSpace(r.Step))
            {
                var step = steps.FirstOrDefault(s => s.Id == r.Step);
                if (step is null)
                    report.Add(file, $"{label}: step '{r.Step}' is not a valid step of this lesson.");
                else if (step is ExplainStep or TraceStep)
                    report.Add(file, $"{label}: step '{r.Step}' is an {(step is TraceStep ? "trace" : "explain")} step; a review item needs a question (choice, predict-output, lowered or benchmark-predict).");
            }
            if (r.Id is not null && r.Concept is not null && r.Step is not null)
                items.Add(new ReviewItem(r.Id, r.Concept, r.Step, kind));
        }
        return items;
    }

    /// <summary>Authors choose between concept and pattern items; re-solves are derived from the learner's mistakes, never declared.</summary>
    private static bool TryParseKind(string text, out ReviewKind kind)
    {
        kind = text switch { "concept" => ReviewKind.Concept, "pattern" => ReviewKind.Pattern, _ => ReviewKind.Concept };
        return text is "concept" or "pattern";
    }

    private static List<string> LoadHints(StepDto dto, string label, string file, ValidationReport report)
    {
        var hints = dto.Hints ?? [];
        if (hints.Count == 0) return [];
        if (dto.Type is not ("choice" or "predict-output" or "lowered" or "benchmark-predict" or "allocations" or "write-code" or "fill-blank" or "parsons"))
            report.Add(file, $"{label}: 'hints' only apply to questions (choice, predict-output, lowered, benchmark-predict, allocations, write-code, fill-blank or parsons).");
        var max = dto.Walkthrough is null ? MaxHints : MaxHints - 1;
        if (hints.Count > max)
            report.Add(file, dto.Walkthrough is null
                ? $"{label}: at most {MaxHints} hints (nudge, pattern hint, partial, full solution); found {hints.Count}."
                : $"{label}: at most {max} hints when there is a 'walkthrough', which is the fourth rung (full solution); found {hints.Count}.");
        for (var i = 0; i < hints.Count; i++)
            if (string.IsNullOrWhiteSpace(hints[i])) report.Add(file, $"{label}: hints[{i}] is empty.");
        return hints.Select(h => h ?? "").ToList();
    }

    private const int MaxHints = 4;

    private static readonly string[] QuestionTypes = ["choice", "predict-output", "lowered", "benchmark-predict", "allocations", "write-code", "fill-blank", "parsons"];

    private static Step? LoadStep(StepDto dto, string where, string file, ValidationReport report,
        Dictionary<string, string> seenIds, bool isAlternative = false)
    {
        var label = dto.Id is null ? where : $"{where} ('{dto.Id}')";
        if (dto.Type is { } type && StepTypes.All.Contains(type) && report.App is { } app && !app.SupportsStepType(type))
            report.Add(file, $"{label}: step type '{type}' is not enabled by app '{app.AppId}' (stepTypes in its manifest).");
        var authoredType = dto.Type;
        if (authoredType == "allocations" && dto.Variant == AllocationsRewrite) PrepareAllocationsRewrite(dto, label, file, report);
        var step = LoadStepBody(dto, label, file, report, seenIds);

        var preferred = dto.LargeScreenPreferred ?? false;
        if (preferred && !QuestionTypes.Contains(dto.Type))
            report.Add(file, $"{label}: 'largeScreenPreferred' only applies to questions (choice, predict-output, lowered, benchmark-predict, allocations, write-code, fill-blank or parsons).");
        if (dto.Compare is not null && dto.Type != "write-code")
            report.Add(file, $"{label}: 'compare' only applies to write-code steps.");
        if (dto.Files is not null && dto.Type != "write-code")
            report.Add(file, $"{label}: 'files' only applies to write-code steps.");
        if (authoredType != "allocations" && (dto.Variant is not null || dto.Input is not null))
            report.Add(file, $"{label}: 'variant' and 'input' only apply to allocations steps.");
        if (authoredType == "allocations" && dto.Variant == AllocationsRewrite && dto.Input is not null)
            report.Add(file, $"{label}: 'input' only applies to variant: {AllocationsPredict}; a rewrite's tests carry their own inputs.");
        if (authoredType != "allocations" && dto.Options?.Any(o => o?.Bytes is not null) == true)
            report.Add(file, $"{label}: an option's 'bytes' only applies to allocations steps.");
        if (dto.Type != "benchmark-predict" && (dto.Measure is not null || dto.Setup is not null || dto.Candidates is not null))
            report.Add(file, $"{label}: 'measure', 'setup' and 'candidates' only apply to benchmark-predict steps.");
        if (isAlternative && preferred)
            report.Add(file, $"{label}: a phone alternative is for phones; it can't be 'largeScreenPreferred' itself.");

        Step? alternative = null;
        if (dto.PhoneAlternative is { } altDto)
        {
            var altWhere = $"{label}: phoneAlternative";
            if (isAlternative) report.Add(file, $"{altWhere}: a phone alternative can't have a phone alternative of its own.");
            else if (!preferred) report.Add(file, $"{altWhere}: only a step with 'largeScreenPreferred: true' has a phone alternative.");
            if (altDto.Type is not ("parsons" or "fill-blank"))
                report.Add(file, $"{altWhere}: the alternative must be a tap-friendly step: parsons or fill-blank (found '{altDto.Type ?? "(missing)"}').");
            // Loaded either way, so a mistake inside it is reported now rather than after the first one is fixed.
            alternative = LoadStep(altDto, altWhere, file, report, seenIds, isAlternative: true);
        }

        // Android cannot run C#; without a tap-friendly alternative a C# write-code step is a dead end on the phone.
        if (!isAlternative && dto.Type == "write-code" && dto.Language == "csharp" && dto.PhoneAlternative is null)
            report.Add(file, $"{label}: a C# write-code step needs 'largeScreenPreferred: true' and a 'phoneAlternative' (parsons or fill-blank), because Android can't run C#.");

        var pattern = LoadPatternTag(dto, label, file, report, isAlternative);
        var cue = LoadPatternCue(dto, label, file, report, isAlternative);
        var difficulty = LoadDifficulty(dto, label, file, report, isAlternative);

        if (step is null || (!preferred && alternative is null && pattern is null && cue is null && difficulty is null)) return step;
        return step with { LargeScreenPreferred = preferred, PhoneAlternative = alternative, Pattern = pattern, PatternCue = cue, Difficulty = difficulty };
    }

    /// <summary>
    /// A step's <c>pattern</c>: which pattern solves it. Whether the pattern exists, and is taught no later than the step, is checked once
    /// every lesson is loaded (<see cref="PatternTags"/>).
    /// </summary>
    private static string? LoadPatternTag(StepDto dto, string label, string file, ValidationReport report, bool isAlternative)
    {
        if (dto.Pattern is null) return null;
        if (string.IsNullOrWhiteSpace(dto.Pattern))
        {
            report.Add(file, $"{label}: 'pattern' is empty; name a pattern ID or remove it.");
            return null;
        }
        if (isAlternative)
            report.Add(file, $"{label}: a phone alternative is solved by the same pattern as its step; put 'pattern' on the step itself.");
        else if (dto.Type is not ("write-code" or "fill-blank" or "parsons"))
            report.Add(file, $"{label}: 'pattern' only applies to problems the learner solves (write-code, fill-blank or parsons).");
        return dto.Pattern;
    }

    /// <summary>
    /// A tagged step's <c>patternCue</c>: what in the prompt points to its pattern, shown after the learner picks one. Required with
    /// <c>pattern</c>, so a wrong pick is always told why, and meaningless without it.
    /// </summary>
    private static string? LoadPatternCue(StepDto dto, string label, string file, ValidationReport report, bool isAlternative)
    {
        if (dto.PatternCue is null)
        {
            if (!string.IsNullOrWhiteSpace(dto.Pattern) && !isAlternative)
                report.Add(file, $"{label}: a step with 'pattern' needs a 'patternCue': what in the prompt points to that pattern.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(dto.PatternCue))
        {
            report.Add(file, $"{label}: 'patternCue' is empty; say what in the prompt points to the pattern, or remove it.");
            return null;
        }
        if (isAlternative)
            report.Add(file, $"{label}: a phone alternative shares its step's pattern; put 'patternCue' on the step itself.");
        else if (dto.Pattern is null)
            report.Add(file, $"{label}: 'patternCue' explains a 'pattern'; add the 'pattern' it explains or remove it.");
        return dto.PatternCue.Trim();
    }

    /// <summary>
    /// A tagged step's <c>difficulty</c> (#128): easy, medium or hard. Optional: without it the step type's default applies
    /// (<see cref="Step.Level"/>), so it is only worth saying when that default is wrong. Meaningless without a <c>pattern</c>.
    /// </summary>
    private static Difficulty? LoadDifficulty(StepDto dto, string label, string file, ValidationReport report, bool isAlternative)
    {
        if (dto.Difficulty is null) return null;
        Difficulty? difficulty = dto.Difficulty.Trim() switch
        {
            "easy" => Difficulty.Easy,
            "medium" => Difficulty.Medium,
            "hard" => Difficulty.Hard,
            _ => null,
        };
        if (difficulty is null)
            report.Add(file, $"{label}: 'difficulty' must be easy, medium or hard (found '{dto.Difficulty}').");
        else if (isAlternative)
            report.Add(file, $"{label}: a phone alternative is the same problem as its step; put 'difficulty' on the step itself.");
        else if (string.IsNullOrWhiteSpace(dto.Pattern))
            report.Add(file, $"{label}: 'difficulty' is for problems Practice offers; add the 'pattern' that solves it or remove it.");
        return difficulty;
    }

    private static Step? LoadStepBody(StepDto dto, string label, string file, ValidationReport report,
        Dictionary<string, string> seenIds)
    {
        var before = report.Errors.Count;

        CheckId(dto.Id, "step", file, report, seenIds, label);
        Require(dto.Title, "title", file, report, label);

        var hints = LoadHints(dto, label, file, report);
        if (dto.Type == "choice") return WithHints(RejectWalkthrough(dto, label, file, report, LoadChoice(dto, label, file, report, before)), hints);
        if (dto.Type == "predict-output") return WithHints(WithWalkthrough(LoadPredictOutput(dto, label, file, report, before), dto, label, file, report), hints);
        if (dto.Type == "lowered") return WithHints(RejectWalkthrough(dto, label, file, report, LoadLowered(dto, label, file, report, before)), hints);
        if (dto.Type == "benchmark-predict") return WithHints(RejectWalkthrough(dto, label, file, report, LoadBenchmarkPredict(dto, label, file, report, before)), hints);
        if (dto.Type == "allocations") return WithHints(RejectWalkthrough(dto, label, file, report, LoadAllocationsPredict(dto, label, file, report, before)), hints);
        if (dto.Type == "write-code") return WithHints(WithWalkthrough(LoadWriteCode(dto, label, file, report, before), dto, label, file, report), hints);
        if (dto.Type == "fill-blank") return WithHints(WithWalkthrough(LoadFillBlank(dto, label, file, report, before), dto, label, file, report), hints);
        if (dto.Type == "parsons") return WithHints(WithWalkthrough(LoadParsons(dto, label, file, report, before), dto, label, file, report), hints);
        if (dto.Type == "trace") return RejectWalkthrough(dto, label, file, report, LoadTrace(dto, label, file, report, before));
        if (dto.Type == "async-timeline") return RejectWalkthrough(dto, label, file, report, LoadAsyncTimeline(dto, label, file, report, before));

        if (dto.Type != "explain")
        {
            report.Add(file, $"{label}: unsupported step type '{dto.Type ?? "(missing)"}' (supported: explain, choice, predict-output, lowered, benchmark-predict, allocations, write-code, fill-blank, parsons, trace, async-timeline).");
            return null;
        }

        RejectWalkthrough(dto, label, file, report, null);
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

        List<LabelledSnippet>? comparison = null;
        if (dto.Comparison is { } entries)
        {
            comparison = [];
            if (entries.Count == 0) report.Add(file, $"{label}: comparison needs at least one labelled snippet.");
            for (var i = 0; i < entries.Count; i++)
            {
                var c = entries[i];
                if (IsEmptyEntry(c, $"{label}: comparison[{i}]", file, report)) continue;
                if (string.IsNullOrWhiteSpace(c.Label)) report.Add(file, $"{label}: comparison[{i}] needs a 'label'.");
                if (string.IsNullOrWhiteSpace(c.Language)) report.Add(file, $"{label}: comparison[{i}] needs a 'language'.");
                if (string.IsNullOrWhiteSpace(c.Code)) report.Add(file, $"{label}: comparison[{i}] needs 'code'.");
                comparison.Add(new LabelledSnippet(c.Label ?? "", c.Language ?? "", c.Code ?? ""));
            }
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

    /// <summary>Steps that have no solution to run (explanations, choices, traces) can't carry a walkthrough.</summary>
    private static Step? RejectWalkthrough(StepDto dto, string label, string file, ValidationReport report, Step? step)
    {
        if (dto.Walkthrough is not null)
            report.Add(file, $"{label}: 'walkthrough' only applies to predict-output, write-code, fill-blank or parsons steps, which have code to walk through.");
        return step;
    }

    /// <summary>
    /// Adds the full-solution rung: the step's own reference code (a write-code solution, a Parsons ordering, a fill-in-the-blank
    /// template with its first accepted answers, a predict-output's code), plus the optional <c>call</c> that exercises it, and
    /// queues it to be run once so a walkthrough that fails can't ship.
    /// </summary>
    private static Step? WithWalkthrough(Step? step, StepDto dto, string label, string file, ValidationReport report)
    {
        if (dto.Walkthrough is not { } w || step is null) return step;
        if (dto.Files is not null) return step;   // refused, with its reason, where the files are read
        var where = $"{label}: walkthrough";

        Require(w.Explanation, "explanation", file, report, where);
        var call = w.Call?.Trim() ?? "";
        if (step is WriteCodeStep && call.Length == 0)
            report.Add(file, $"{where}: a write-code walkthrough needs a 'call' that runs the solution, e.g. {dto.Entrypoint}(...).");

        var visuals = LoadVisuals(w.Visualise, where, file, report, missing: null);
        var reference = step switch
        {
            WriteCodeStep => dto.Solution ?? "",
            ParsonsStep p => string.Join('\n', p.Lines.Select(l => new string(' ', 4 * l.Level) + l.Text)),
            FillBlankStep f => FillBlankTemplate.Fill(f.Template, id => f.Blanks.First(b => b.Id == id).Accepted[0]),
            PredictOutputStep predict => predict.Code.Code,
            _ => "",
        };
        var language = step switch
        {
            WriteCodeStep write => write.Language,
            ParsonsStep parsons => parsons.Language,
            FillBlankStep fill => fill.Language,
            PredictOutputStep predict => predict.Code.Language,
            _ => "python",
        };
        if (w.Language is not null && w.Language != language)
            report.Add(file, $"{where}: language '{w.Language}' must match the step's language '{language}'.");
        CheckRuntimeLanguage(language, where, file, report);
        CheckMemoryLanguage(visuals, language, where, file, report);

        var code = reference.TrimEnd() + "\n" + (call.Length > 0 ? "\n" + call + "\n" : "");
        report.TraceChecks.Add(new TraceCheck(file, where, code, visuals, language, step.Id, IsWalkthrough: true));
        return step with { Walkthrough = new Walkthrough(code, visuals, w.Explanation ?? "") { Language = w.Language ?? (language == "python" ? null : language) } };
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
        var (options, accepted, mistakes) = LoadPrediction(dto, label, file, report);

        return report.Errors.Count == errorsBefore
            ? new PredictOutputStep(dto.Id!, dto.Title!, dto.Prompt!, new CodeSnippet(dto.Language ?? "python", dto.Code!), accepted, mistakes, options)
            : null;
    }

    /// <summary>
    /// A <c>lowered</c> step: a question about what the C# compiler made of the snippet. It is answered like a prediction (typed or
    /// multiple choice). The lowered C# and IL are never authored; the pack build compiles the snippet and attaches them.
    /// </summary>
    private static Step? LoadLowered(StepDto dto, string label, string file, ValidationReport report, int errorsBefore)
    {
        Require(dto.Prompt, "prompt", file, report, label);
        Require(dto.Code, "code", file, report, label);
        if (dto.Language is not null and not "csharp")
            report.Add(file, $"{label}: a lowered step shows what the C# compiler produced; 'language' must be csharp (or left out).");
        var (options, accepted, mistakes) = LoadPrediction(dto, label, file, report);

        if (report.Errors.Count != errorsBefore) return null;
        report.LoweringChecks.Add(new LoweringCheck(file, label, dto.Code!, dto.Id!));
        return new LoweredStep(dto.Id!, dto.Title!, dto.Prompt!, dto.Code!, accepted, mistakes, options);
    }

    /// <summary>
    /// A <c>benchmark-predict</c> step: the learner ranks implementations, then sees how BenchmarkDotNet measured them. The right order is
    /// never authored; the pack build runs the candidates and the learner is graded on what they showed.
    /// </summary>
    private static Step? LoadBenchmarkPredict(StepDto dto, string label, string file, ValidationReport report, int errorsBefore)
    {
        Require(dto.Prompt, "prompt", file, report, label);
        if (dto.Language is not null and not "csharp")
            report.Add(file, $"{label}: a benchmark step measures C# with BenchmarkDotNet; 'language' must be csharp (or left out).");
        if (dto.Accepted is { Count: > 0 } || dto.Options is { Count: > 0 } || dto.Mistakes is { Count: > 0 })
            report.Add(file, $"{label}: a benchmark step is answered by ranking its candidates; remove 'accepted', 'options' and 'mistakes' (the right order is what the benchmark records).");

        var measure = dto.Measure ?? BenchmarkPredictStep.Time;
        if (measure is not (BenchmarkPredictStep.Time or BenchmarkPredictStep.Allocations))
            report.Add(file, $"{label}: 'measure' must be time or allocations (found '{measure}').");

        var candidates = new List<BenchmarkCandidate>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < (dto.Candidates?.Count ?? 0); i++)
        {
            var c = dto.Candidates![i];
            if (IsEmptyEntry(c, $"{label}: candidates[{i}]", file, report)) continue;
            if (string.IsNullOrWhiteSpace(c.Id) || !CandidateIdPattern().IsMatch(c.Id))
                report.Add(file, $"{label}: candidates[{i}] needs an 'id' of lowercase letters, digits and hyphens (found '{c.Id}').");
            else if (!ids.Add(c.Id))
                report.Add(file, $"{label}: candidates[{i}] repeats the id '{c.Id}'.");
            if (string.IsNullOrWhiteSpace(c.Label)) report.Add(file, $"{label}: candidates[{i}] needs a 'label' (what the learner sees).");
            if (string.IsNullOrWhiteSpace(c.Code)) report.Add(file, $"{label}: candidates[{i}] needs 'code': a method body that returns an int.");
            candidates.Add(new BenchmarkCandidate(c.Id ?? "", c.Label ?? "", c.Code ?? ""));
        }
        if (candidates.Count < 2 || candidates.Count > CSharpBenchmark.MaxCandidates)
            report.Add(file, $"{label}: a benchmark step needs 2 to {CSharpBenchmark.MaxCandidates} candidates (found {candidates.Count}).");

        if (report.Errors.Count != errorsBefore) return null;
        var setup = string.IsNullOrWhiteSpace(dto.Setup) ? null : dto.Setup;
        report.BenchmarkChecks.Add(new BenchmarkCheck(file, label, measure, setup, candidates, dto.Id!));
        return new BenchmarkPredictStep(dto.Id!, dto.Title!, dto.Prompt!, measure, setup, candidates);
    }

    private const string AllocationsPredict = "predict";
    private const string AllocationsRewrite = "rewrite";

    /// <summary>
    /// An <c>allocations</c> step with <c>variant: rewrite</c> is a C# write-code exercise graded by an allocation budget (#151), so it is loaded
    /// as one: the budget is what the learner is asked to meet, so at least one test must declare it. It follows every write-code rule,
    /// including the phone alternative a C# exercise needs (Android can't run C#).
    /// </summary>
    private static void PrepareAllocationsRewrite(StepDto dto, string label, string file, ValidationReport report)
    {
        if (dto.Language is not null and not "csharp")
            report.Add(file, $"{label}: an allocations step measures C# allocations; 'language' must be csharp (or left out).");
        else dto.Language = "csharp";
        if (!(dto.Tests?.Any(t => t?.MaxAllocatedBytes is not null) ?? false))
            report.Add(file, $"{label}: a rewrite is graded by its allocation budget; give at least one test a 'maxAllocatedBytes' (0 for code that allocates nothing).");
        dto.Type = "write-code";
    }

    /// <summary>
    /// An <c>allocations</c> step with <c>variant: predict</c>: "how much does one call allocate?", answered by picking a byte count. The
    /// snippet is the code shown; <c>entrypoint</c> and <c>input</c> say which call is counted. The right option is never authored: the
    /// pack build calls the snippet and marks the option that names what the C# runtime counted, so the answer cannot drift from the code.
    /// Nothing runs on the learner's device, so the step needs no phone alternative.
    /// </summary>
    private static Step? LoadAllocationsPredict(StepDto dto, string label, string file, ValidationReport report, int errorsBefore)
    {
        if (dto.Variant is not AllocationsPredict)
            report.Add(file, dto.Variant is null
                ? $"{label}: an allocations step needs a 'variant': {AllocationsPredict} (pick how much a snippet allocates) or {AllocationsRewrite} (rewrite it to allocate less)."
                : $"{label}: 'variant' must be {AllocationsPredict} or {AllocationsRewrite} (found '{dto.Variant}').");
        Require(dto.Prompt, "prompt", file, report, label);
        Require(dto.Code, "code", file, report, label);
        Require(dto.Entrypoint, "entrypoint", file, report, label);
        if (dto.Language is not null and not "csharp")
            report.Add(file, $"{label}: an allocations step measures C# allocations; 'language' must be csharp (or left out).");
        if (!string.IsNullOrWhiteSpace(dto.Entrypoint) && !EntrypointIdentifier.IsMatch(dto.Entrypoint))
            report.Add(file, $"{label}: entrypoint '{dto.Entrypoint}' is not a valid function name (use letters, digits and underscores; start with a letter or underscore).");
        if (dto.Accepted is { Count: > 0 } || dto.Mistakes is { Count: > 0 } || dto.Tests is { Count: > 0 } || dto.Starter is not null || dto.Solution is not null || dto.Files is not null)
            report.Add(file, $"{label}: a predict allocations step is answered by picking a byte count; remove 'accepted', 'mistakes', 'tests', 'starter', 'solution' and 'files' (those belong to variant: {AllocationsRewrite}).");

        var options = new List<AllocationOption>();
        var seen = new HashSet<long>();
        for (var i = 0; i < (dto.Options?.Count ?? 0); i++)
        {
            var o = dto.Options![i];
            if (IsEmptyEntry(o, $"{label}: options[{i}]", file, report)) continue;
            if (o.Text is not null || o.Correct is not null)
                report.Add(file, $"{label}: options[{i}] gives a byte count only ('bytes', and optionally 'feedback'); the right option is the one the measurement matches.");
            if (o.Bytes is not { } bytes) report.Add(file, $"{label}: options[{i}] needs 'bytes': the whole number of bytes it claims one call allocates.");
            else if (bytes is < 0 or > ReferenceSolutionChecker.MaxPredictedBytes)
                report.Add(file, $"{label}: options[{i}] 'bytes' must be between 0 and {ReferenceSolutionChecker.MaxPredictedBytes:N0}.");
            else if (!seen.Add(bytes)) report.Add(file, $"{label}: options[{i}] repeats {ReferenceSolutionChecker.Bytes(bytes)}; each option needs its own byte count.");
            else options.Add(new AllocationOption(bytes, string.IsNullOrWhiteSpace(o.Feedback) ? null : o.Feedback));
        }
        if ((dto.Options?.Count ?? 0) is < 2 or > MaxAllocationOptions)
            report.Add(file, $"{label}: an allocations step needs 2 to {MaxAllocationOptions} options (found {dto.Options?.Count ?? 0}).");

        if (report.Errors.Count != errorsBefore) return null;
        var input = dto.Input ?? "";
        report.AllocationChecks.Add(new AllocationCheck(file, label, dto.Id!, dto.Code!, dto.Entrypoint!, input, options));
        // The right option is marked once the snippet has been measured (see ValidationReport.AllocationOptions).
        var pending = options.Select(o => new ChoiceOption(ReferenceSolutionChecker.Bytes(o.Bytes), false, o.Feedback)).ToList();
        return new PredictOutputStep(dto.Id!, dto.Title!, $"{dto.Prompt}\n\nMeasured call: {dto.Entrypoint}({input})", new CodeSnippet("csharp", dto.Code!), [], [], pending);
    }

    private const int MaxAllocationOptions = 6;

    /// <summary>The answer side shared by predict-output and lowered steps: typed 'accepted' answers with 'mistakes', or multiple-choice 'options'.</summary>
    private static (List<ChoiceOption> Options, List<string> Accepted, List<MistakePattern> Mistakes) LoadPrediction(
        StepDto dto, string label, string file, ValidationReport report)
    {
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

            mistakes = LoadMistakes(dto.Mistakes, accepted, label, file, report);
        }

        return (options, accepted, mistakes);
    }

    /// <summary>Validates typed-answer mistake patterns against the answers the same prompt or blank accepts.</summary>
    private static List<MistakePattern> LoadMistakes(List<MistakeDto>? dtos, List<string> accepted, string label, string file, ValidationReport report)
    {
        var mistakes = new List<MistakePattern>();
        var acceptedNormalised = accepted.Select(AnswerEvaluator.Normalize).ToHashSet();
        for (var i = 0; i < (dtos?.Count ?? 0); i++)
        {
            var m = dtos![i];
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
        return mistakes;
    }

    private static Step? LoadFillBlank(StepDto dto, string label, string file, ValidationReport report, int errorsBefore)
    {
        Require(dto.Prompt, "prompt", file, report, label);
        Require(dto.Template, "template", file, report, label);
        var language = dto.Language ?? "python";
        CheckLanguage(language, label, file, report);

        var blanks = new List<Blank>();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (dto.Blanks is null or { Count: 0 })
            report.Add(file, $"{label}: a fill-blank step needs at least one entry in 'blanks'.");
        else
            for (var i = 0; i < dto.Blanks.Count; i++)
            {
                var b = dto.Blanks[i];
                if (IsEmptyEntry(b, $"{label}: blanks[{i}]", file, report)) continue;
                var where = b.Id is null ? $"{label}: blanks[{i}]" : $"{label}: blanks[{i}] ('{b.Id}')";
                if (string.IsNullOrWhiteSpace(b.Id)) report.Add(file, $"{where}: 'id' is required.");
                else if (!FillBlankTemplate.IsValidBlankId(b.Id))
                    report.Add(file, $"{where}: id '{b.Id}' is invalid; use lowercase words joined by '-' (e.g. loop-start).");
                else if (!declared.Add(b.Id)) report.Add(file, $"{where}: duplicate blank id '{b.Id}'.");

                var accepted = new List<string>();
                if (b.Accepted is null or { Count: 0 })
                    report.Add(file, $"{where}: needs at least one accepted answer.");
                else
                    for (var j = 0; j < b.Accepted.Count; j++)
                    {
                        var answer = b.Accepted[j] ?? "";
                        var normalised = AnswerEvaluator.Normalize(answer);
                        if (normalised.Length == 0) report.Add(file, $"{where}: accepted[{j}] is empty.");
                        else if (normalised.Contains('\n')) report.Add(file, $"{where}: accepted[{j}] spans several lines; a blank is a single line.");
                        else if (accepted.Any(a => AnswerEvaluator.Normalize(a) == normalised))
                            report.Add(file, $"{where}: accepted[{j}] repeats another accepted answer.");
                        accepted.Add(answer);
                    }
                var mistakes = LoadMistakes(b.Mistakes, accepted, where, file, report);
                blanks.Add(new Blank(b.Id ?? "", accepted, mistakes));
            }

        if (!string.IsNullOrWhiteSpace(dto.Template))
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var marker in FillBlankTemplate.Markers(dto.Template))
            {
                if (!FillBlankTemplate.IsValidBlankId(marker))
                    report.Add(file, $"{label}: template marker '{{{{{marker}}}}}' is not a valid blank; use {{{{blank-id}}}} with lowercase words joined by '-'.");
                else if (!declared.Contains(marker))
                    report.Add(file, $"{label}: template uses blank '{marker}' which is not declared in 'blanks'.");
                else if (!used.Add(marker))
                    report.Add(file, $"{label}: blank '{marker}' appears more than once in the template.");
            }
            foreach (var id in declared.Where(id => !used.Contains(id)))
                report.Add(file, $"{label}: blank '{id}' is declared but never used in the template.");
        }

        return report.Errors.Count == errorsBefore
            ? new FillBlankStep(dto.Id!, dto.Title!, dto.Prompt!, language, dto.Template!, blanks)
            : null;
    }

    private static Step? LoadParsons(StepDto dto, string label, string file, ValidationReport report, int errorsBefore)
    {
        Require(dto.Prompt, "prompt", file, report, label);
        Require(dto.Solution, "solution", file, report, label);
        var language = dto.Language ?? "python";
        CheckLanguage(language, label, file, report);

        var lines = string.IsNullOrWhiteSpace(dto.Solution)
            ? []
            : ParsonsSolution.Read(dto.Solution, msg => report.Add(file, $"{label}: {msg}"), language);

        return report.Errors.Count == errorsBefore
            ? new ParsonsStep(dto.Id!, dto.Title!, dto.Prompt!, language, lines)
            : null;
    }

    private static Step? LoadTrace(StepDto dto, string label, string file, ValidationReport report, int errorsBefore)
    {
        Require(dto.Body, "body", file, report, label);
        Require(dto.Code, "code", file, report, label);

        var visuals = LoadVisuals(dto.Visualise, label, file, report,
            missing: $"{label}: a trace step needs at least one entry in 'visualise' (which variable to draw, and how).");

        if (report.Errors.Count != errorsBefore) return null;
        var language = dto.Language ?? "python";
        CheckRuntimeLanguage(language, label, file, report);
        CheckMemoryLanguage(visuals, language, label, file, report);
        if (report.Errors.Count != errorsBefore) return null;
        report.TraceChecks.Add(new TraceCheck(file, label, dto.Code!, visuals, language, dto.Id ?? "", IsWalkthrough: false));
        return new TraceStep(dto.Id!, dto.Title!, dto.Body!, dto.Code!, visuals) { Language = dto.Language };
    }

    private static Step? LoadAsyncTimeline(StepDto dto, string label, string file, ValidationReport report, int errorsBefore)
    {
        Require(dto.Body, "body", file, report, label);
        var events = new List<AsyncTimelineEvent>();
        var sequences = new HashSet<int>();
        for (var i = 0; i < (dto.Events?.Count ?? 0); i++)
        {
            var item = dto.Events![i];
            if (IsEmptyEntry(item, $"{label}: events[{i}]", file, report)) continue;
            var where = $"{label}: events[{i}]";
            if (item.Sequence is not > 0) report.Add(file, $"{where} needs a positive 'sequence'.");
            else if (!sequences.Add(item.Sequence.Value)) report.Add(file, $"{where} repeats sequence {item.Sequence.Value}.");
            if (string.IsNullOrWhiteSpace(item.Thread)) report.Add(file, $"{where} needs a 'thread'.");
            if (string.IsNullOrWhiteSpace(item.Label)) report.Add(file, $"{where} needs a 'label'.");
            if (!TryParseAsyncTimelineKind(item.Kind, out var kind)) report.Add(file, $"{where}: 'kind' must be enter, await, continuation-queued, resume, complete or blocking-wait.");
            if (kind is AsyncTimelineEventKind.Await or AsyncTimelineEventKind.ContinuationQueued or AsyncTimelineEventKind.BlockingWait)
                if (string.IsNullOrWhiteSpace(item.Operation)) report.Add(file, $"{where}: '{item.Kind}' needs an 'operation'.");
            if (kind == AsyncTimelineEventKind.ContinuationQueued && string.IsNullOrWhiteSpace(item.ContinuationThread))
                report.Add(file, $"{where}: continuation-queued needs a 'continuationThread'.");
            events.Add(new AsyncTimelineEvent(item.Sequence ?? 0, item.Thread ?? "", kind, item.Label ?? "", item.Operation, item.ContinuationThread, item.State));
        }
        if (events.Count == 0) report.Add(file, $"{label}: an async-timeline step needs at least one recorded event.");
        else if (events.All(e => string.IsNullOrWhiteSpace(e.State))) report.Add(file, $"{label}: an async-timeline needs at least one recorded state-machine 'state'.");
        if (dto.Code is not null || dto.Language is not null || dto.Visualise is not null)
            report.Add(file, $"{label}: async-timeline uses recorded 'events'; remove trace-only 'code', 'language' and 'visualise'.");
        return report.Errors.Count == errorsBefore ? new AsyncTimelineStep(dto.Id!, dto.Title!, dto.Body!, events.OrderBy(e => e.Sequence).ToList()) : null;
    }

    private static bool TryParseAsyncTimelineKind(string? text, out AsyncTimelineEventKind kind)
    {
        kind = text switch
        {
            "enter" => AsyncTimelineEventKind.Enter,
            "await" => AsyncTimelineEventKind.Await,
            "continuation-queued" => AsyncTimelineEventKind.ContinuationQueued,
            "resume" => AsyncTimelineEventKind.Resume,
            "complete" => AsyncTimelineEventKind.Complete,
            "blocking-wait" => AsyncTimelineEventKind.BlockingWait,
            _ => AsyncTimelineEventKind.Enter,
        };
        return text is "enter" or "await" or "continuation-queued" or "resume" or "complete" or "blocking-wait";
    }

    /// <summary>Reads a <c>visualise</c> list. <paramref name="missing"/> is the error to report when the list is empty, or null when drawing nothing is fine.</summary>
    private static List<Visual> LoadVisuals(List<VisualDto>? list, string label, string file, ValidationReport report, string? missing)
    {
        var visuals = new List<Visual>();
        if (list is null or { Count: 0 })
        {
            if (missing is not null) report.Add(file, missing);
        }
        else
            for (var i = 0; i < list.Count; i++)
            {
                var v = list[i];
                var where = $"{label}: visualise[{i}]";
                if (IsEmptyEntry(v, where, file, report)) continue;
                Require(v.As, "as", file, report, where);
                if (!string.IsNullOrWhiteSpace(v.As) && !Visual.Kinds.Contains(v.As))
                    report.Add(file, $"{where}: unsupported 'as' value '{v.As}' (supported: {string.Join(", ", Visual.Kinds)}).");

                // The call stack and memory draw the calls and objects of the run, not a variable, and each is drawn once.
                if (v.As is Visual.CallStack or Visual.Memory)
                {
                    if (v.Variable is not null)
                        report.Add(file, $"{where}: a {v.As} draws the {(v.As == Visual.Memory ? "stack and heap" : "calls under way")}, not a variable; leave 'variable' out.");
                    if (v.Pointers is { Count: > 0 })
                        report.Add(file, $"{where}: a {v.As} has no pointers.");
                    if (visuals.FindIndex(x => x.As == v.As) is var drawn and >= 0)
                        report.Add(file, $"{where}: the {(v.As == Visual.Memory ? "memory view" : "call stack")} is already drawn by visualise[{drawn}].");
                }
                else
                {
                    Require(v.Variable, "variable", file, report, where);
                    if (!string.IsNullOrWhiteSpace(v.Variable) && !EntrypointIdentifier.IsMatch(v.Variable))
                        report.Add(file, $"{where}: variable '{v.Variable}' is not a valid Python name.");
                    if (v.Variable is not null && visuals.FindIndex(x => x.Variable == v.Variable) is var first and >= 0)
                        report.Add(file, $"{where}: '{v.Variable}' is already drawn by visualise[{first}].");
                }

                var functions = new List<string>();
                foreach (var function in v.Functions ?? [])
                {
                    if (v.As != Visual.CallStack)
                    {
                        report.Add(file, $"{where}: 'functions' is an option of a {Visual.CallStack} only, not of '{v.As}'.");
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(function) || !EntrypointIdentifier.IsMatch(function))
                        report.Add(file, $"{where}: function '{function}' is not a valid Python name.");
                    else if (functions.Contains(function))
                        report.Add(file, $"{where}: function '{function}' is listed twice.");
                    else
                        functions.Add(function);
                }

                // A linked list's and a tree's options say how to walk their nodes; no other drawing has nodes.
                var isList = v.As == Visual.LinkedList;
                var isTree = v.As == Visual.Tree;
                foreach (var (option, value, owners) in new[]
                         {
                             ("next", v.Next, new[] { Visual.LinkedList }), ("value", v.Value, [Visual.LinkedList, Visual.Tree]),
                             ("left", v.Left, [Visual.Tree]), ("right", v.Right, [Visual.Tree]),
                         })
                {
                    if (value is null) continue;
                    if (!owners.Contains(v.As))
                        report.Add(file, $"{where}: '{option}' is an option of a {string.Join(" or ", owners)} only, not of '{v.As}'.");
                    else if (!EntrypointIdentifier.IsMatch(value))
                        report.Add(file, $"{where}: {option} '{value}' is not a valid Python attribute name.");
                }
                if (isTree && (v.Left ?? Visual.DefaultLeft) == (v.Right ?? Visual.DefaultRight))
                    report.Add(file, $"{where}: 'left' and 'right' name the same attribute; a tree node needs two links.");
                // A graph's options say how its edges run and which nodes a search has reached; 'visited' is shared with a tree.
                var isGraph = v.As == Visual.Graph;
                if (v.Visited is not null)
                {
                    if (!isTree && !isGraph)
                        report.Add(file, $"{where}: 'visited' is an option of a {Visual.Tree} or {Visual.Graph} only, not of '{v.As}'.");
                    else if (!EntrypointIdentifier.IsMatch(v.Visited))
                        report.Add(file, $"{where}: visited '{v.Visited}' is not a valid Python name.");
                    else if (v.Visited == v.Variable)
                        report.Add(file, $"{where}: visited '{v.Visited}' is the {v.As}'s own variable; name the list or set of the nodes visited.");
                }
                foreach (var (option, given) in new[] { ("directed", v.Directed is not null), ("weights", v.Weights is not null), ("edge", v.Edge is not null), ("frontier", v.Frontier is not null) })
                    if (given && !isGraph)
                        report.Add(file, $"{where}: '{option}' is an option of a {Visual.Graph} only, not of '{v.As}'.");
                EdgeNames? edgeNames = null;
                if (isGraph && v.Edge is not null)
                {
                    if (v.Edge is not { Count: 2 })
                        report.Add(file, $"{where}: 'edge' is a pair of variables, the node an edge leaves and the node it reaches: [node, neighbour].");
                    else if (v.Edge.Any(n => string.IsNullOrWhiteSpace(n) || !EntrypointIdentifier.IsMatch(n)))
                        report.Add(file, $"{where}: edge [{string.Join(", ", v.Edge)}] has a name that is not a valid Python name.");
                    else if (v.Edge.Contains(v.Variable))
                        report.Add(file, $"{where}: edge [{string.Join(", ", v.Edge)}] names the graph's own variable; name the variables holding its two nodes.");
                    else if (v.Edge[0] == v.Edge[1])
                        report.Add(file, $"{where}: edge [{string.Join(", ", v.Edge)}] names one variable twice; an edge has two ends.");
                    else
                        edgeNames = new EdgeNames(v.Edge[0], v.Edge[1]);
                }
                if (isGraph && v.Frontier is not null)
                {
                    if (!EntrypointIdentifier.IsMatch(v.Frontier))
                        report.Add(file, $"{where}: frontier '{v.Frontier}' is not a valid Python name.");
                    else if (v.Frontier == v.Variable)
                        report.Add(file, $"{where}: frontier '{v.Frontier}' is the graph's own variable; name the queue or stack of nodes waiting.");
                    else if (v.Frontier == v.Visited)
                        report.Add(file, $"{where}: frontier '{v.Frontier}' is also 'visited'; name the nodes waiting, not the ones reached.");
                }

                var isHeap = v.As == Visual.Heap;
                if (v.Max is not null && !isHeap)
                    report.Add(file, $"{where}: 'max' is an option of a {Visual.Heap} only, not of '{v.As}'.");

                // A grid's options say where its cells are pointed at, labelled and shaded; no other drawing has cells in rows.
                var isGrid = v.As == Visual.Grid;
                var cells = new List<CellPair>();
                foreach (var pair in v.Cells ?? [])
                {
                    if (!isGrid)
                    {
                        report.Add(file, $"{where}: 'cells' is an option of a {Visual.Grid} only, not of '{v.As}'.");
                        break;
                    }
                    if (pair is not { Count: 2 })
                        report.Add(file, $"{where}: a 'cells' entry is a pair of int variables, the row's and the column's: [i, j].");
                    else if (pair.Any(n => string.IsNullOrWhiteSpace(n) || !EntrypointIdentifier.IsMatch(n)))
                        report.Add(file, $"{where}: cells pair [{string.Join(", ", pair)}] has a name that is not a valid Python name.");
                    else if (pair.Contains(v.Variable))
                        report.Add(file, $"{where}: cells pair [{string.Join(", ", pair)}] names the grid's own variable; name the int variables indexing it.");
                    else if (cells.Contains(new CellPair(pair[0], pair[1])))
                        report.Add(file, $"{where}: cells pair [{string.Join(", ", pair)}] is listed twice.");
                    else
                        cells.Add(new CellPair(pair[0], pair[1]));
                }
                foreach (var (option, value) in new[] { ("rowLabels", v.RowLabels), ("colLabels", v.ColLabels), ("highlight", v.Highlight) })
                {
                    if (value is null) continue;
                    if (!isGrid)
                        report.Add(file, $"{where}: '{option}' is an option of a {Visual.Grid} only, not of '{v.As}'.");
                    else if (!EntrypointIdentifier.IsMatch(value))
                        report.Add(file, $"{where}: {option} '{value}' is not a valid Python name.");
                    else if (value == v.Variable)
                        report.Add(file, $"{where}: {option} '{value}' is the grid's own variable; name another.");
                }
                if (v.LabelOffset is { } offset)
                {
                    if (!isGrid)
                        report.Add(file, $"{where}: 'labelOffset' is an option of a {Visual.Grid} only, not of '{v.As}'.");
                    else if (offset < 0)
                        report.Add(file, $"{where}: labelOffset {offset} is negative; it is the row and column the first label goes on.");
                    else if (v.RowLabels is null && v.ColLabels is null)
                        report.Add(file, $"{where}: labelOffset is given but there are no labels; add 'rowLabels' or 'colLabels', or leave it out.");
                }

                var pointers = new List<string>();
                foreach (var pointer in v.Pointers ?? [])
                {
                    if (string.IsNullOrWhiteSpace(pointer) || !EntrypointIdentifier.IsMatch(pointer))
                        report.Add(file, $"{where}: pointer '{pointer}' is not a valid Python name.");
                    else if (pointers.Contains(pointer))
                        report.Add(file, $"{where}: pointer '{pointer}' is listed twice.");
                    else if (isList && pointer == v.Variable)
                        report.Add(file, $"{where}: pointer '{pointer}' is the list's own variable, which is already drawn on its first node.");
                    else if (isTree && pointer == v.Variable)
                        report.Add(file, $"{where}: pointer '{pointer}' is the tree's own variable, which is already drawn on its root.");
                    else if (isGraph && pointer == v.Variable)
                        report.Add(file, $"{where}: pointer '{pointer}' is the graph's own variable; a graph pointer holds one of its nodes.");
                    else if (isHeap && pointer == v.Variable)
                        report.Add(file, $"{where}: pointer '{pointer}' is the heap's own variable; a heap pointer is an int variable indexing it.");
                    else if (isGrid && pointer == v.Variable)
                        report.Add(file, $"{where}: pointer '{pointer}' is the grid's own variable; a grid pointer holds a (row, col) pair.");
                    else
                        pointers.Add(pointer);
                }
                visuals.Add(new Visual(v.As is Visual.CallStack or Visual.Memory ? "" : v.Variable ?? "", v.As ?? "", pointers,
                    v.Next ?? Visual.DefaultNext, v.Value ?? Visual.DefaultValue, functions,
                    v.Left ?? Visual.DefaultLeft, v.Right ?? Visual.DefaultRight, v.Visited)
                {
                    Cells = cells, RowLabels = v.RowLabels, ColLabels = v.ColLabels, LabelOffset = v.LabelOffset ?? 0, Highlight = v.Highlight,
                    Directed = v.Directed ?? false, Weights = v.Weights ?? false, Edge = edgeNames, Frontier = isGraph ? v.Frontier : null,
                    Max = isHeap && (v.Max ?? false),
                });
            }

        return visuals;
    }

    /// <summary>The memory view shows what the C# recorder captures (value types, references, boxes); Python has no such distinction to draw.</summary>
    private static void CheckMemoryLanguage(IEnumerable<Visual> visuals, string language, string label, string file, ValidationReport report)
    {
        if (language != "csharp" && visuals.Any(v => v.As == Visual.Memory))
            report.Add(file, $"{label}: a {Visual.Memory} view is for C# code (it draws value types, references and boxes); this step's language is {language}.");
    }

    private static void CheckLanguage(string language, string label, string file, ValidationReport report)
    {
        if (language is not ("python" or "csharp"))
            report.Add(file, $"{label}: unsupported language '{language}' (supported: python, csharp).");
    }

    private static void CheckRuntimeLanguage(string language, string label, string file, ValidationReport report)
    {
        CheckLanguage(language, label, file, report);
        if (report.App is { } app && !app.SupportsRuntime(language))
            report.Add(file, $"{label}: runtime '{language}' is not enabled by app '{app.AppId}'.");
    }

    private const int MaxAllocationBudget = 1_000_000;
    private static readonly Regex EntrypointIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static Step? LoadWriteCode(StepDto dto, string label, string file, ValidationReport report, int errorsBefore)
    {
        Require(dto.Prompt, "prompt", file, report, label);
        Require(dto.Entrypoint, "entrypoint", file, report, label);
        var files = dto.Files is null ? null : LoadFiles(dto, label, file, report);
        if (dto.Files is null) Require(dto.Solution, "solution", file, report, label); // proved against the tests; packed only as part of a 'walkthrough', if the step has one
        if (dto.Language is null) report.Add(file, $"{label}: 'language' is required (supported: python, csharp).");
        else CheckRuntimeLanguage(dto.Language, label, file, report);
        if (!string.IsNullOrWhiteSpace(dto.Entrypoint) && !EntrypointIdentifier.IsMatch(dto.Entrypoint))
            report.Add(file, $"{label}: entrypoint '{dto.Entrypoint}' is not a valid function name (use letters, digits and underscores; start with a letter or underscore).");

        if (dto.Compare is not null && dto.Language != "python")
            report.Add(file, $"{label}: 'compare' is a Python harness option; other languages use their runtime's equality.");
        if (dto.Compare is not null && dto.Compare != CodeTest.CompareLinkedList)
            report.Add(file, $"{label}: unknown 'compare' '{dto.Compare}' (supported: {CodeTest.CompareLinkedList}, for a function that returns the head of a linked list).");
        var compare = dto.Compare == CodeTest.CompareLinkedList ? dto.Compare : null;

        var tests = new List<CodeTest>();
        if (dto.Tests is null or { Count: 0 })
            report.Add(file, $"{label}: a write-code step needs at least one hidden test.");
        else
            for (var i = 0; i < dto.Tests.Count; i++)
            {
                var t = dto.Tests[i];
                if (IsEmptyEntry(t, $"{label}: tests[{i}]", file, report)) continue;
                // 'input' may be empty (a function with no arguments); 'expected' never is.
                if (t.Input is null) report.Add(file, $"{label}: tests[{i}] needs 'input' (the call's arguments as source in the exercise language; \"\" for none).");
                if (string.IsNullOrWhiteSpace(t.Expected)) report.Add(file, $"{label}: tests[{i}] needs 'expected' (an expression in the exercise language).");
                if (t.MaxAllocatedBytes is { } bytes)
                {
                    if (dto.Language != "csharp")
                        report.Add(file, $"{label}: tests[{i}] 'maxAllocatedBytes' is measured by the C# runtime; {dto.Language ?? "this"} exercises can't declare an allocation budget.");
                    else if (bytes is < 0 or > MaxAllocationBudget)
                        report.Add(file, $"{label}: tests[{i}] 'maxAllocatedBytes' must be between 0 and {MaxAllocationBudget:N0} (bytes allocated by one call).");
                }
                tests.Add(new CodeTest(t.Input ?? "", t.Expected ?? "", compare, t.MaxAllocatedBytes));
            }

        if (report.Errors.Count != errorsBefore) return null;
        if (files is not null)
        {
            report.ReferenceChecks.Add(new ReferenceCheck(file, label, dto.Id!, dto.Entrypoint!, "", tests, dto.Language!, files.Select(f => new CodeFile(f.Name, f.Solution)).ToList()));
            var starters = files.Select(f => new CodeFile(f.Name, f.Starter)).ToList();
            return new WriteCodeStep(dto.Id!, dto.Title!, dto.Prompt!, dto.Language!, starters[0].Content, dto.Entrypoint!, tests) { Files = starters };
        }
        report.ReferenceChecks.Add(new ReferenceCheck(file, label, dto.Id!, dto.Entrypoint!, dto.Solution!, tests, dto.Language!));
        return new WriteCodeStep(dto.Id!, dto.Title!, dto.Prompt!, dto.Language!, dto.Starter ?? "", dto.Entrypoint!, tests);
    }

    private sealed record AuthoredFile(string Name, string Starter, string Solution);

    /// <summary>
    /// A multi-file exercise's <c>files</c>: each has a <c>name</c>, the <c>starter</c> the learner begins with and the <c>solution</c> the
    /// reference check compiles together with the others'. Only C# has them, and they replace the step's own <c>starter</c> and <c>solution</c>.
    /// </summary>
    private static List<AuthoredFile>? LoadFiles(StepDto dto, string label, string file, ValidationReport report)
    {
        var where = $"{label}: files";
        if (dto.Language is not null && dto.Language != "csharp")
            report.Add(file, $"{where}: only C# exercises can have several files ({dto.Language} exercises are a single 'starter' and 'solution').");
        if (dto.Starter is not null || dto.Solution is not null)
            report.Add(file, $"{where}: a multi-file exercise keeps its code in 'files'; remove the step's own 'starter' and 'solution'.");
        if (dto.Walkthrough is not null)
            report.Add(file, $"{label}: a multi-file exercise can't have a 'walkthrough' (it shows one file of code).");
        var entries = dto.Files!;
        if (entries.Count is < 2 or > CodeFiles.MaxFiles)
        {
            report.Add(file, $"{where}: list between 2 and {CodeFiles.MaxFiles} files (use 'starter' and 'solution' for one).");
            return null;
        }
        var result = new List<AuthoredFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var at = $"{where}[{i}]";
            if (IsEmptyEntry(entry, at, file, report)) continue;
            if (CodeFiles.NameProblem(entry.Name) is { } why) report.Add(file, $"{at}: {why}");
            else if (!seen.Add(entry.Name!)) report.Add(file, $"{at}: file name '{entry.Name}' is used twice (names are compared ignoring case).");
            if (string.IsNullOrWhiteSpace(entry.Solution)) report.Add(file, $"{at}: needs a 'solution' (the file's code in the reference solution).");
            result.Add(new AuthoredFile(entry.Name ?? "", entry.Starter ?? "", entry.Solution ?? ""));
        }
        // Drafts are kept under "<step>/<name>"; one that the server would refuse could never sync.
        var longest = result.Select(f => f.Name.Length).DefaultIfEmpty().Max();
        if ((dto.Id?.Length ?? 0) + 1 + longest > MaxFileDraftKey)
            report.Add(file, $"{where}: the step id plus a file name is too long for a draft to sync (at most {MaxFileDraftKey} characters including the '/').");
        return result;
    }

    // The server limits a draft's key to 100 characters; Practice, Review and Interview put a prefix of their own in front of the step's.
    private const int MaxFileDraftKey = 64;

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

    private sealed class PackDto { public string? Id { get; set; } public string? Version { get; set; } public string? TargetFramework { get; set; } public string? LangVersion { get; set; } }
    private sealed class TrackDto { public string? Id { get; set; } public string? Title { get; set; } }
    private sealed class LessonDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public List<StepDto>? Steps { get; set; }
        public List<ConceptDto>? Concepts { get; set; }
        public List<ReviewItemDto>? ReviewItems { get; set; }
        public List<PatternDto>? Patterns { get; set; }
    }
    private sealed class PracticeDto { public List<StepDto>? Problems { get; set; } }
    private sealed class PatternDto { public string? Id { get; set; } public string? Title { get; set; } }
    private sealed class ConceptDto { public string? Id { get; set; } public string? Title { get; set; } }
    private sealed class ReviewItemDto { public string? Id { get; set; } public string? Concept { get; set; } public string? Step { get; set; } public string? Kind { get; set; } }
    private sealed class SnippetDto { public string? Language { get; set; } public string? Code { get; set; } }
    private sealed class ComparisonDto { public string? Label { get; set; } public string? Language { get; set; } public string? Code { get; set; } }
    private sealed class StepDto
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public List<SnippetDto>? Snippets { get; set; }
        public List<ComparisonDto>? Comparison { get; set; }
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
        public string? Compare { get; set; }
        public string? Solution { get; set; }
        public List<FileDto>? Files { get; set; }
        public string? Template { get; set; }
        public List<BlankDto>? Blanks { get; set; }
        public List<TestDto>? Tests { get; set; }
        public List<VisualDto>? Visualise { get; set; }
        public WalkthroughDto? Walkthrough { get; set; }
        public bool? LargeScreenPreferred { get; set; }
        public StepDto? PhoneAlternative { get; set; }
        public string? Pattern { get; set; }
        public string? PatternCue { get; set; }
        public string? Difficulty { get; set; }
        public string? Measure { get; set; }
        public string? Variant { get; set; }
        public string? Input { get; set; }
        public string? Setup { get; set; }
        public List<CandidateDto>? Candidates { get; set; }
        public List<AsyncTimelineEventDto>? Events { get; set; }
    }
    private sealed class AsyncTimelineEventDto { public int? Sequence { get; set; } public string? Thread { get; set; } public string? Kind { get; set; } public string? Label { get; set; } public string? Operation { get; set; } public string? ContinuationThread { get; set; } public string? State { get; set; } }
    private sealed class CandidateDto { public string? Id { get; set; } public string? Label { get; set; } public string? Code { get; set; } }
    private sealed class FileDto { public string? Name { get; set; } public string? Starter { get; set; } public string? Solution { get; set; } }
    private sealed class WalkthroughDto { public string? Language { get; set; } public string? Call { get; set; } public List<VisualDto>? Visualise { get; set; } public string? Explanation { get; set; } }
    private sealed class VisualDto
    {
        public string? Variable { get; set; }
        public string? As { get; set; }
        public List<string>? Pointers { get; set; }
        public string? Next { get; set; }
        public string? Value { get; set; }
        public List<string>? Functions { get; set; }
        public string? Left { get; set; }
        public string? Right { get; set; }
        public string? Visited { get; set; }
        public List<List<string>>? Cells { get; set; }
        public string? RowLabels { get; set; }
        public string? ColLabels { get; set; }
        public int? LabelOffset { get; set; }
        public string? Highlight { get; set; }
        public bool? Directed { get; set; }
        public bool? Weights { get; set; }
        public List<string>? Edge { get; set; }
        public string? Frontier { get; set; }
        public bool? Max { get; set; }
    }
    private sealed class BlankDto { public string? Id { get; set; } public List<string>? Accepted { get; set; } public List<MistakeDto>? Mistakes { get; set; } }
    private sealed class TestDto { public string? Input { get; set; } public string? Expected { get; set; } public int? MaxAllocatedBytes { get; set; } }
    private sealed class MistakeDto { public List<string>? Answers { get; set; } public string? Regex { get; set; } public string? Feedback { get; set; } }
    private sealed class OptionDto { public string? Text { get; set; } public bool? Correct { get; set; } public string? Feedback { get; set; } public int? Bytes { get; set; } }
}
