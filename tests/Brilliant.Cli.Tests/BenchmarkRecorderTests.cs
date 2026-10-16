using Perfolizer.Horology;
using BenchmarkDotNet.Jobs;
using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

/// <summary>
/// Tests that run BenchmarkDotNet. It counts allocations process-wide and times with the machine's cores, so these must not share the
/// process with other tests running at the same moment: xUnit runs a collection that disables parallelization on its own.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BenchmarkCollection
{
    public const string Name = "benchmarks";
}

/// <summary>
/// The benchmark recorder runs BenchmarkDotNet for real, so its numbers differ on every run. These tests check the recording's shape
/// (every candidate measured, positive times, allocation counts, a machine label), never a figure (#155).
/// </summary>
[Collection(BenchmarkCollection.Name)]
public sealed class BenchmarkRecorderTests
{
    /// <summary>A shorter job than the pack build uses: the same stages, with fewer and briefer iterations, so a test run takes seconds.</summary>
    private static readonly Job Quick = Job.ShortRun.WithWarmupCount(1).WithIterationCount(2).WithIterationTime(TimeInterval.FromMilliseconds(25));

    private const string Setup = "int[] data = Enumerable.Range(0, 1000).ToArray();";

    private static readonly BenchmarkCandidate[] Candidates =
    [
        new("loop", "for loop", "var total = 0;\nfor (var i = 0; i < data.Length; i++) total += data[i];\nreturn total;"),
        new("linq", "LINQ", "return data.Where(x => x % 2 == 0).Select(x => x * 2).ToList().Count;"),
    ];

    [Fact]
    public void A_recording_measures_every_candidate_and_says_where_it_ran()
    {
        var (recording, error) = CSharpBenchmark.Record(BenchmarkPredictStep.Allocations, Setup, Candidates, Quick);

        Assert.Null(error);
        Assert.NotNull(recording);
        Assert.Equal(["loop", "linq"], recording.Rows.Select(r => r.CandidateId).Order(StringComparer.Ordinal).Reverse());
        Assert.All(recording.Rows, row =>
        {
            Assert.True(row.MeanNs > 0, $"{row.CandidateId} mean");
            Assert.True(row.StdDevNs >= 0, $"{row.CandidateId} standard deviation");
            Assert.True(row.AllocatedBytes >= 0, $"{row.CandidateId} allocations");
        });
        Assert.False(string.IsNullOrWhiteSpace(recording.Machine));
        Assert.StartsWith(".NET", recording.Runtime);
        Assert.False(string.IsNullOrWhiteSpace(recording.Job));
    }

    /// <summary>Uses the job the pack build uses: with the quick one a handful of invocations leave stray bytes in an allocation-free loop's count.</summary>
    [Fact]
    public void Allocations_are_counted_per_call_and_the_loop_allocates_nothing()
    {
        var (recording, _) = CSharpBenchmark.Record(BenchmarkPredictStep.Allocations, Setup, Candidates);

        Assert.Equal(0, recording!.Rows.Single(r => r.CandidateId == "loop").AllocatedBytes);
        Assert.True(recording.Rows.Single(r => r.CandidateId == "linq").AllocatedBytes > 0);
    }

    [Fact]
    public void Candidates_that_do_not_compile_are_named_with_their_own_line()
    {
        var broken = new BenchmarkCandidate[] { Candidates[0], new("bad", "Broken", "var x = 1;\nreturn \"two\";") };

        var message = CSharpBenchmark.Check(Setup, broken);

        Assert.NotNull(message);
        Assert.Contains("candidate 'bad', line 2", message);
        Assert.Contains("CS0029", message);
    }

    [Fact]
    public void A_setup_that_does_not_compile_is_reported_as_the_setup()
    {
        var message = CSharpBenchmark.Check("int[] data = Missing();", Candidates);

        Assert.Contains("the setup, line 1", message);
    }

    [Fact]
    public void Compilable_candidates_pass_the_check_without_running_anything()
    {
        Assert.Null(CSharpBenchmark.Check(Setup, Candidates));
    }

    [Fact]
    public void Too_many_candidates_are_refused()
    {
        var many = Enumerable.Range(0, CSharpBenchmark.MaxCandidates + 1).Select(i => new BenchmarkCandidate("c" + i, "C" + i, "return " + i + ";")).ToList();

        Assert.Contains("at most", CSharpBenchmark.Check(null, many));
    }

    [Fact]
    public void A_time_ranking_whose_neighbours_cannot_be_told_apart_is_refused()
    {
        const string Sum = "var total = 0;\nfor (var i = 0; i < data.Length; i++) total += data[i];\nreturn total;";
        BenchmarkCandidate[] twins = [new("one", "First", Sum), new("two", "Second", Sum)];

        var (_, error) = CSharpBenchmark.Record(BenchmarkPredictStep.Time, Setup, twins, Quick);

        Assert.Contains("too close to call", error);
    }

    [Fact]
    public void A_candidate_too_fast_to_time_is_refused_for_a_time_ranking()
    {
        BenchmarkCandidate[] trivial = [Candidates[0], new("nothing", "Nothing", "return 1 + data.Length;")];

        var (_, error) = CSharpBenchmark.Record(BenchmarkPredictStep.Time, Setup, trivial, Quick);

        Assert.Contains("too fast to time", error);
        Assert.Contains("Nothing", error);
    }
}
