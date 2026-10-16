using System.Text.Json;
using Brilliant.Core.Content;

namespace Brilliant.Core.Tests;

/// <summary>The learner ranks implementations; the pack's recorded benchmark decides the right order (#155).</summary>
public sealed class BenchmarkPredictTests
{
    private static readonly IReadOnlyList<BenchmarkCandidate> Candidates =
    [
        new("loop", "for loop", "return 1;"),
        new("linq", "LINQ", "return 2;"),
        new("sort", "sort first", "return 3;"),
    ];

    private static BenchmarkRow Row(string id, double meanNs, long bytes) => new(id, meanNs, meanNs / 20, bytes);

    private static BenchmarkPredictStep Step(string measure, params BenchmarkRow[] rows) =>
        new("step.bench", "Rank", "Fastest first.", measure, null, Candidates)
        {
            Recorded = new BenchmarkRecording(rows, "Apple M4 Max, macOS 26 (Arm64)", ".NET 10.0.10", "ShortRun"),
        };

    private static BenchmarkPredictStep ByTime() => Step("time", Row("loop", 300, 0), Row("linq", 1200, 48), Row("sort", 9000, 4000));

    [Fact]
    public void The_recorded_order_is_correct_for_time_fastest_first()
    {
        Assert.True(AnswerEvaluator.Evaluate(ByTime(), ["loop", "linq", "sort"]).IsCorrect);
    }

    [Fact]
    public void The_recorded_ranking_follows_the_measure_not_the_candidate_order()
    {
        var step = Step("time", Row("loop", 9000, 0), Row("linq", 300, 48), Row("sort", 1200, 4000));

        Assert.True(AnswerEvaluator.Evaluate(step, ["linq", "sort", "loop"]).IsCorrect);
        Assert.False(AnswerEvaluator.Evaluate(step, ["loop", "linq", "sort"]).IsCorrect);
    }

    [Fact]
    public void A_wrong_order_is_wrong_and_says_how_many_places_are_right()
    {
        var result = AnswerEvaluator.Evaluate(ByTime(), ["loop", "sort", "linq"]);

        Assert.False(result.IsCorrect);
        Assert.Contains("1 of 3", result.Feedback);
    }

    [Fact]
    public void Allocations_rank_by_bytes_and_equal_bytes_are_a_tie_in_either_order()
    {
        var step = Step("allocations", Row("loop", 300, 0), Row("linq", 1200, 48), Row("sort", 900, 0));

        Assert.True(AnswerEvaluator.Evaluate(step, ["loop", "sort", "linq"]).IsCorrect);
        Assert.True(AnswerEvaluator.Evaluate(step, ["sort", "loop", "linq"]).IsCorrect);
        Assert.False(AnswerEvaluator.Evaluate(step, ["loop", "linq", "sort"]).IsCorrect);
    }

    [Fact]
    public void A_ranking_that_leaves_out_or_repeats_a_candidate_is_wrong()
    {
        Assert.False(AnswerEvaluator.Evaluate(ByTime(), ["loop", "linq"]).IsCorrect);
        Assert.False(AnswerEvaluator.Evaluate(ByTime(), ["loop", "loop", "linq"]).IsCorrect);
        Assert.False(AnswerEvaluator.Evaluate(ByTime(), ["loop", "linq", "other"]).IsCorrect);
        Assert.False(AnswerEvaluator.Evaluate(ByTime(), []).IsCorrect);
    }

    [Fact]
    public void A_step_without_a_recording_cannot_be_graded()
    {
        var step = new BenchmarkPredictStep("step.bench", "Rank", "Fastest first.", "time", null, Candidates);

        Assert.Throws<InvalidOperationException>(() => AnswerEvaluator.Evaluate(step, ["loop", "linq", "sort"]));
    }

    [Fact]
    public void The_recorded_order_lists_each_candidate_with_its_numbers()
    {
        var order = ByTime().RankedRows();

        Assert.Equal(["loop", "linq", "sort"], order.Select(r => r.CandidateId));
        Assert.Equal(48, order[1].AllocatedBytes);
    }

    [Fact]
    public void The_step_and_its_recording_survive_a_pack_round_trip()
    {
        Step step = ByTime();

        var json = JsonSerializer.Serialize(step, ContentPackFormat.JsonOptions);
        var back = Assert.IsType<BenchmarkPredictStep>(JsonSerializer.Deserialize<Step>(json, ContentPackFormat.JsonOptions));

        Assert.Contains("\"benchmark-predict\"", json);
        Assert.Equal("Apple M4 Max, macOS 26 (Arm64)", back.Recorded!.Machine);
        Assert.Equal(3, back.Recorded.Rows.Count);
        Assert.Equal(1200, back.Recorded.Rows[1].MeanNs);
    }
}
