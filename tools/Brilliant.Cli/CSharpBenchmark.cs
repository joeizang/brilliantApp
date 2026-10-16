using System.Reflection;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Basic.Reference.Assemblies;
using Brilliant.Core.Code;
using Brilliant.Core.Content;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Brilliant.Cli;

/// <summary>
/// Measures a <c>benchmark-predict</c> step's candidates when the pack is built (#155). The candidates are written into one class, compiled
/// in Release with the pinned compiler, loaded from memory, and handed to BenchmarkDotNet's in-process toolchain, which skips the
/// per-benchmark project build that its default toolchain needs. What comes back is mean time, standard deviation and bytes allocated per
/// call for each candidate, plus the machine and runtime they were measured on.
/// Measurements differ from run to run, so nothing downstream depends on exact figures: the pack records one run and the step is graded
/// on the order that run produced. A time ranking whose neighbours are too close to call is refused rather than recorded.
/// </summary>
internal static class CSharpBenchmark
{
    public const int MaxCandidates = 5;
    public const int MaxSourceLength = 8_192;

    /// <summary>Neighbouring candidates in a time ranking must differ by at least this factor, or run-to-run noise could reorder them.</summary>
    public const double MinimumTimeRatio = 1.25;

    /// <summary>
    /// A mean below this is the timer's own noise (the JIT folded the work away, or it is a few instructions), and a ranking built on it
    /// would not survive a rerun.
    /// </summary>
    public const double MinimumMeanNs = 10;

    private const string Usings = "using System;\nusing System.Collections.Generic;\nusing System.Linq;\nusing BenchmarkDotNet.Attributes;\n";

    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
        Net100.References.All.Cast<MetadataReference>()
            // The attributes live in BenchmarkDotNet.Annotations ([Benchmark]) and BenchmarkDotNet itself ([MemoryDiagnoser]).
            .Concat(new[] { typeof(BenchmarkAttribute), typeof(MemoryDiagnoserAttribute) }.Select(t => t.Assembly).Distinct()
                .Select(a => MetadataReference.CreateFromFile(a.Location)))
            .ToList());

    /// <summary>BenchmarkDotNet's own short job: three warm-up and three measured iterations, enough to rank clearly different code in seconds.</summary>
    public static Job DefaultJob { get; } = Job.ShortRun;

    /// <summary>Why the candidates can't be benchmarked (they don't compile, are too long), or null. Nothing is run.</summary>
    public static string? Check(string? setup, IReadOnlyList<BenchmarkCandidate> candidates)
    {
        if (candidates.Count > MaxCandidates) return $"a benchmark takes at most {MaxCandidates} candidates.";
        if (candidates.Sum(c => c.Code.Length) + (setup?.Length ?? 0) > MaxSourceLength)
            return $"the benchmarked code is over {MaxSourceLength} characters; trim it to what the comparison is about.";
        var (image, error) = Compile(setup, candidates);
        return image is null ? error : null;
    }

    /// <summary>Measures every candidate with <paramref name="job"/>; the error says what failed, naming the candidate.</summary>
    public static (BenchmarkRecording? Recording, string? Error) Record(string measure, string? setup, IReadOnlyList<BenchmarkCandidate> candidates, Job? job = null)
    {
        var (image, error) = Compile(setup, candidates);
        if (image is null) return (null, error);

        var artifacts = Path.Combine(Path.GetTempPath(), "brilliant-bench-" + Guid.NewGuid().ToString("N"));
        try
        {
            var type = Assembly.Load(image).GetType(ClassName)!;
            var toolchainJob = (job ?? DefaultJob).WithToolchain(InProcessNoEmitToolchain.Instance);
            var config = ManualConfig.CreateEmpty().AddJob(toolchainJob).AddColumnProvider(DefaultColumnProviders.Instance)
                .WithOptions(ConfigOptions.DisableOptimizationsValidator).WithArtifactsPath(artifacts);
            var summary = BenchmarkRunner.Run(type, config);

            var rows = new List<BenchmarkRow>();
            foreach (var report in summary.Reports)
            {
                var index = MethodIndex(report.BenchmarkCase.Descriptor.WorkloadMethod.Name);
                if (report.ResultStatistics is not { } stats || index is null || index >= candidates.Count)
                    return (null, "BenchmarkDotNet could not measure every candidate" + (summary.HasCriticalValidationErrors ? " (it rejected the benchmark class)." : "."));
                rows.Add(new BenchmarkRow(candidates[index.Value].Id, stats.Mean, stats.StandardDeviation,
                    report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) ?? 0));
            }
            if (rows.Count != candidates.Count) return (null, "BenchmarkDotNet could not measure every candidate.");

            var machine = MachineLabel(summary.HostEnvironmentInfo);
            var recording = new BenchmarkRecording(rows, machine, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, toolchainJob.ToString());
            return (recording, TooCloseToCall(measure, candidates, rows));
        }
        finally
        {
            try { Directory.Delete(artifacts, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Where the numbers come from, as a learner reads it: CPU, operating system and architecture, e.g.
    /// "Apple M4 Max, macOS Tahoe 26.6.2, Arm64". BenchmarkDotNet's first information line is "BenchmarkDotNet vX, &lt;OS&gt;"; only the OS is kept.
    /// </summary>
    private static string MachineLabel(HostEnvironmentInfo host)
    {
        var os = host.ToFormattedString().FirstOrDefault() is { } line && line.IndexOf(", ", StringComparison.Ordinal) is var comma and >= 0 ? line[(comma + 2)..] : "unknown OS";
        var cpu = host.Cpu.Value?.ProcessorName;
        return $"{(string.IsNullOrWhiteSpace(cpu) ? "unknown CPU" : cpu)}, {os}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}";
    }

    private const string ClassName = "BenchmarkCandidates";

    private static int? MethodIndex(string name) =>
        name.StartsWith("Candidate", StringComparison.Ordinal) && int.TryParse(name.AsSpan("Candidate".Length), out var i) ? i : null;

    /// <summary>For a time ranking, the pair of neighbours whose means are within <see cref="MinimumTimeRatio"/> of each other, as an error message.</summary>
    private static string? TooCloseToCall(string measure, IReadOnlyList<BenchmarkCandidate> candidates, IReadOnlyList<BenchmarkRow> rows)
    {
        if (measure != BenchmarkPredictStep.Time) return null;
        if (rows.FirstOrDefault(r => r.MeanNs < MinimumMeanNs) is { } trivial)
            return $"'{candidates.First(c => c.Id == trivial.CandidateId).Label}' is too fast to time ({trivial.MeanNs:N2} ns per call; the minimum is {MinimumMeanNs:N0} ns). " +
                   "Give it more work (a larger input, a loop) or rank by allocations.";
        var ranked = rows.OrderBy(r => r.MeanNs).ToList();
        for (var i = 1; i < ranked.Count; i++)
            if (ranked[i].MeanNs < ranked[i - 1].MeanNs * MinimumTimeRatio)
            {
                string Name(BenchmarkRow row) => candidates.First(c => c.Id == row.CandidateId).Label;
                return $"'{Name(ranked[i - 1])}' ({ranked[i - 1].MeanNs:N0} ns) and '{Name(ranked[i])}' ({ranked[i].MeanNs:N0} ns) are too close to call; " +
                       $"neighbours in a time ranking must differ by at least {MinimumTimeRatio}x so a rerun cannot reorder them. Change a candidate or rank by allocations.";
            }
        return null;
    }

    private static (byte[]? Image, string? Error) Compile(string? setup, IReadOnlyList<BenchmarkCandidate> candidates)
    {
        if (!LanguageVersionFacts.TryParse(CSharpPins.LanguageVersion, out var languageVersion)) return (null, "unsupported pinned C# language version.");

        // #line maps every diagnostic back to the author's own text: the file name is the candidate id (or "setup") and the line is theirs.
        var source = new StringBuilder(Usings).Append("[MemoryDiagnoser]\npublic class ").Append(ClassName).Append("\n{\n");
        if (!string.IsNullOrWhiteSpace(setup)) source.Append("#line 1 \"setup\"\n").Append(setup).Append("\n#line hidden\n");
        for (var i = 0; i < candidates.Count; i++)
            source.Append("[Benchmark] public int Candidate").Append(i).Append("()\n{\n#line 1 \"").Append(candidates[i].Id).Append("\"\n")
                .Append(candidates[i].Code).Append("\n#line hidden\n}\n");
        source.Append("}\n");

        var tree = CSharpSyntaxTree.ParseText(source.ToString(), new CSharpParseOptions(languageVersion));
        var compilation = CSharpCompilation.Create("BenchmarkCandidates", [tree], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: true,
                allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable));

        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (emitted.Success) return (image.ToArray(), null);

        var error = emitted.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
        var mapped = error.Location.GetMappedLineSpan();
        var where = mapped.HasMappedPath ? $"{(mapped.Path == "setup" ? "the setup" : $"candidate '{mapped.Path}'")}, line {mapped.StartLinePosition.Line + 1}: " : "";
        return (null, $"the benchmark does not compile ({where}{error.Id} {error.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}).");
    }
}
