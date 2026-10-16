using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The benchmark-predict step (#155): rank the candidates, then see what was measured and where.</summary>
public class BenchmarkPredictStepViewTests : ShortcutContext
{
    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    // Recorded fastest to slowest: loop (900 ns), span (2.4 µs), linq (48 µs).
    private static readonly BenchmarkPredictStep Time = new("step.b", "Loop or LINQ", "Rank them, fastest first.", BenchmarkPredictStep.Time, null,
        [new("linq", "LINQ chain", "return data.Where(x => x > 1).Count();"), new("loop", "for loop", "var n = 0; foreach (var x in data) n++; return n;"), new("span", "Span", "return data.AsSpan().Length;")])
    {
        Recorded = new BenchmarkRecording(
            [new("linq", 48_000, 1_000, 120), new("loop", 900, 10, 0), new("span", 2_400, 30, 0)], "Apple M4 Max, macOS 26", ".NET 10.0.0", "ShortRun"),
    };

    private readonly MemoryLog _log = new();

    public BenchmarkPredictStepViewTests() => Services.AddSingleton(new ProgressRecorder(_log, "device"));

    private static string[] Order(IRenderedComponent<BenchmarkPredictStepView> cut) =>
        cut.FindAll("li.candidate strong").Select(e => e.TextContent).ToArray();

    private static void MoveUp(IRenderedComponent<BenchmarkPredictStepView> cut, string label)
    {
        var index = Array.IndexOf(Order(cut), label);
        cut.FindAll("li.candidate")[index].QuerySelector("button[aria-label='Move up']")!.Click();
    }

    /// <summary>Puts the list in loop, span, linq order by moving each into place.</summary>
    private static void Solve(IRenderedComponent<BenchmarkPredictStepView> cut)
    {
        foreach (var label in new[] { "for loop", "Span" })
            while (Array.IndexOf(Order(cut), label) > Array.IndexOf(new[] { "for loop", "Span" }, label)) MoveUp(cut, label);
    }

    [Fact]
    public void It_never_starts_on_the_answer_and_starts_the_same_way_each_time()
    {
        var first = Order(Render<BenchmarkPredictStepView>(p => p.Add(c => c.Step, Time)));
        var again = Order(Render<BenchmarkPredictStepView>(p => p.Add(c => c.Step, Time)));

        Assert.NotEqual(["for loop", "Span", "LINQ chain"], first);
        Assert.Equal(first, again);
        Assert.Equal(3, first.Length);
    }

    [Fact]
    public void Every_starting_order_for_every_step_is_a_wrong_answer()
    {
        for (var n = 0; n < 40; n++)
        {
            var step = Time with { Id = "step." + n };
            var order = BenchmarkPredictStepView.Shuffled(step);
            Assert.False(AnswerEvaluator.Evaluate(step, order.Select(c => c.Id).ToList()).IsCorrect, step.Id);
        }
    }

    [Fact]
    public void A_wrong_order_says_how_many_places_are_right_and_keeps_the_order_for_another_go()
    {
        BenchmarkPredictStepView.Attempt? seen = null;
        var cut = Render<BenchmarkPredictStepView>(p => p.Add(c => c.Step, Time).Add(c => c.OnAnswered, a => seen = a));
        var before = Order(cut);

        cut.Find("button.primary").Click();

        Assert.Contains("Not quite", cut.Find(".verdict").TextContent);
        Assert.Contains("in the right place", cut.Find(".verdict").TextContent);
        Assert.False(seen!.Result.IsCorrect);
        Assert.Empty(cut.FindAll(".benchmark-results"));   // the numbers wait for the right answer

        cut.Find("button.primary").Click();   // Try again
        Assert.Equal(before, Order(cut));
    }

    [Fact]
    public void The_right_order_shows_the_measurements_with_the_machine_they_came_from()
    {
        BenchmarkPredictStepView.Attempt? seen = null;
        var cut = Render<BenchmarkPredictStepView>(p => p.Add(c => c.Step, Time).Add(c => c.OnAnswered, a => seen = a));

        Solve(cut);
        cut.Find("button.primary").Click();

        Assert.Contains("Correct", cut.Find(".verdict").TextContent);
        Assert.Equal(["loop", "span", "linq"], seen!.Ranking);
        var rows = cut.FindAll(".benchmark-results tbody tr");
        Assert.Equal(["loop", "span", "linq"], rows.Select(r => r.GetAttribute("data-candidate")!).ToArray());
        Assert.Contains("900 ns", rows[0].TextContent);
        Assert.Contains("2.4 µs", rows[1].TextContent);
        Assert.Contains("48 µs", rows[2].TextContent);
        Assert.Contains("120 B", rows[2].TextContent);
        Assert.Contains("Apple M4 Max, macOS 26", cut.Find(".machine").TextContent);
        Assert.Contains(".NET 10.0.0", cut.Find(".machine").TextContent);
    }

    [Fact]
    public void Tied_allocations_may_be_answered_in_either_order()
    {
        var step = Time with { Measure = BenchmarkPredictStep.Allocations };
        var cut = Render<BenchmarkPredictStepView>(p => p.Add(c => c.Step, step));

        foreach (var label in new[] { "Span", "for loop" })   // span and loop both allocate 0 B: either may lead
            while (Array.IndexOf(Order(cut), label) > Array.IndexOf(new[] { "Span", "for loop" }, label)) MoveUp(cut, label);
        cut.Find("button.primary").Click();

        Assert.Contains("Correct", cut.Find(".verdict").TextContent);
    }

    [Fact]
    public void The_arrow_buttons_stop_at_the_ends_and_lock_once_checked()
    {
        var cut = Render<BenchmarkPredictStepView>(p => p.Add(c => c.Step, Time));

        Assert.True(cut.FindAll("li.candidate")[0].QuerySelector("button[aria-label='Move up']")!.HasAttribute("disabled"));
        Assert.True(cut.FindAll("li.candidate")[2].QuerySelector("button[aria-label='Move down']")!.HasAttribute("disabled"));

        cut.Find("button.primary").Click();

        Assert.All(cut.FindAll("li.candidate button"), b => Assert.True(b.HasAttribute("disabled")));
    }

    [Fact]
    public void Alt_arrow_keys_move_the_selected_candidate()
    {
        var cut = Render<BenchmarkPredictStepView>(p => p.Add(c => c.Step, Time));
        var before = Order(cut);

        cut.FindAll("li.candidate")[1].KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowUp", AltKey = true });

        Assert.Equal([before[1], before[0], before[2]], Order(cut));
    }

    [Fact]
    public void A_pack_without_the_recording_says_so_instead_of_asking_an_unanswerable_question()
    {
        var cut = Render<BenchmarkPredictStepView>(p => p.Add(c => c.Step, Time with { Recorded = null }));

        Assert.Contains("aren't in this pack", cut.Find(".benchmark-missing").TextContent);
        var continued = false;
        cut.Render(p => p.Add(c => c.OnContinue, () => continued = true));
        cut.Find("button.primary").Click();   // a hand-built pack must not dead-end the session
        Assert.True(continued);
    }

    [Fact]
    public void The_measure_being_ranked_is_named_in_the_instructions()
    {
        Assert.Contains("fastest first", Render<BenchmarkPredictStepView>(p => p.Add(c => c.Step, Time)).Find(".hint-text").TextContent);
        Assert.Contains("fewest allocated bytes first",
            Render<BenchmarkPredictStepView>(p => p.Add(c => c.Step, Time with { Measure = BenchmarkPredictStep.Allocations })).Find(".hint-text").TextContent);
    }

    [Fact]
    public void It_works_on_a_phone_too()
    {
        Screen.Set(ScreenClass.Phone);

        var cut = Render<BenchmarkPredictStepView>(p => p.Add(c => c.Step, Time));

        Assert.Equal(3, cut.FindAll("li.candidate").Count);
        Assert.DoesNotContain("⌥", cut.Find(".hint-text").TextContent);
    }

    private static Lesson LessonWith(BenchmarkPredictStep step) => new("lesson.b", "Lesson", "track.t", [step])
    {
        Concepts = [new Concept("concept.c", "C")],
        ReviewItems = [new ReviewItem("review.b", "concept.c", step.Id)],
    };

    [Fact]
    public void Answering_in_a_lesson_is_recorded_with_the_ranking_but_not_when_replaying_it()
    {
        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, LessonWith(Time)));
        cut.Find("button.primary").Click();

        var recorded = Assert.Single(_log.Items, e => e.Type == ProgressEventTypes.StepAnswered && e.StepId == "step.b");
        Assert.Contains("\"ranking\"", recorded.Data);

        _log.Items.Clear();
        var replay = Render<LessonViewer>(p => p.Add(c => c.Lesson, LessonWith(Time)).Add(c => c.Review, true));
        replay.Find("button.primary").Click();
        Assert.DoesNotContain(_log.Items, e => e.Type == ProgressEventTypes.StepAnswered);
    }

    [Fact]
    public void Review_asks_a_benchmark_question_instead_of_skipping_it()
    {
        var lesson = LessonWith(Time);
        var content = new ContentGraph(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [lesson.Id])], [lesson]);
        Services.GetRequiredService<ProgressRecorder>().CompleteStep(lesson, Time.Id);

        var cut = Render<ReviewView>(p => p.Add(c => c.Content, content));

        Assert.DoesNotContain("can't be reviewed", cut.Markup);
        Assert.NotEmpty(cut.FindAll("li.candidate"));
    }

    [Fact]
    public void A_practice_style_prompt_shows_the_benchmark_question()
    {
        var cut = Render<ProblemPrompt>(p => p.Add(c => c.Step, Time));

        Assert.Equal("Rank them, fastest first.", cut.Find(".prompt").TextContent);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(120, "120 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(3_145_728, "3 MB")]
    public void Bytes_read_in_the_unit_that_keeps_them_short(long bytes, string expected) => Assert.Equal(expected, BenchmarkFormat.Bytes(bytes));

    [Theory]
    [InlineData(0.5, "0.5 ns")]
    [InlineData(900, "900 ns")]
    [InlineData(2400, "2.4 µs")]
    [InlineData(1_500_000, "1.5 ms")]
    public void Times_read_in_the_unit_that_keeps_them_short(double ns, string expected) => Assert.Equal(expected, BenchmarkFormat.Time(ns));
}
