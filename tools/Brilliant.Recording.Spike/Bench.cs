using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace Brilliant.Recording.Spike;

/// <summary>The two-method comparison the spike benchmarks: a hand-written loop against the equivalent LINQ call.</summary>
[MemoryDiagnoser]
public class SumBenchmarks
{
    int[] _data = [];

    [GlobalSetup] public void Setup() => _data = Enumerable.Range(0, 1000).ToArray();

    [Benchmark(Baseline = true)]
    public int ForLoop()
    {
        var total = 0;
        for (var i = 0; i < _data.Length; i++) total += _data[i];
        return total;
    }

    [Benchmark]
    public int Linq() => _data.Where(x => x % 2 == 0).Sum();
}

static class Bench
{
    public sealed record Row(string Method, double MeanNs, double StdDevNs, long AllocatedBytes, int Iterations);
    public sealed record Output(IReadOnlyList<Row> Rows, string Host, string Runtime, string Job, double TotalMs);

    public static Output Run()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // In-process toolchain: no per-benchmark `dotnet build`, which is what makes it usable from a CLI at pack time.
        // A short job keeps the run to seconds; it widens the error bars, which the report calls out.
        var job = Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance);
        var config = ManualConfig.CreateEmpty().AddJob(job).AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Instance)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);
        var summary = BenchmarkRunner.Run<SumBenchmarks>(config);
        var rows = summary.Reports.Select(r => new Row(
            r.BenchmarkCase.Descriptor.WorkloadMethod.Name,
            r.ResultStatistics!.Mean, r.ResultStatistics.StandardDeviation,
            r.GcStats.GetBytesAllocatedPerOperation(r.BenchmarkCase) ?? -1,
            r.ResultStatistics.N)).ToList();
        return new Output(rows, string.Join("; ", BenchmarkDotNet.Environments.HostEnvironmentInfo.GetCurrent().ToFormattedString()), summary.HostEnvironmentInfo.DotNetSdkVersion.Value,
            job.ToString(), sw.Elapsed.TotalMilliseconds);
    }
}
