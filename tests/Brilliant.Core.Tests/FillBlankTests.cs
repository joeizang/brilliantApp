using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Tests;

public class FillBlankTests
{
    private static readonly FillBlankStep Step = new("step.fb", "FB", "Complete the loop", "python",
        "for i in {{iter}}:\n    total {{op}} i\n",
        [
            new Blank("iter", ["range(1, n + 1)", "range(1,n+1)"], [new MistakePattern([], @"^range\(n\)$", "range(n) starts at 0 and stops before n.")]),
            new Blank("op", ["+="], [new MistakePattern(["="], null, "A plain = replaces the total each time.")]),
        ]);

    private static FillBlankResult Evaluate(string iter, string op) =>
        AnswerEvaluator.Evaluate(Step, new Dictionary<string, string> { ["iter"] = iter, ["op"] = op });

    [Fact]
    public void Every_blank_right_is_correct()
    {
        var result = Evaluate("range(1, n + 1)", "+=");
        Assert.True(result.IsCorrect);
        Assert.All(result.Blanks, b => Assert.True(b.IsCorrect));
    }

    [Fact]
    public void Each_blank_is_judged_on_its_own()
    {
        var result = Evaluate("range(1, n + 1)", "=");

        Assert.False(result.IsCorrect);
        Assert.True(result.Blanks.Single(b => b.BlankId == "iter").IsCorrect);
        var op = result.Blanks.Single(b => b.BlankId == "op");
        Assert.False(op.IsCorrect);
        Assert.Equal("A plain = replaces the total each time.", op.Feedback);
    }

    [Fact]
    public void Answers_are_normalised_and_any_accepted_answer_counts()
    {
        Assert.True(Evaluate("  range(1,n+1) ", "+=").IsCorrect);
        Assert.True(Evaluate("range(1,   n + 1)", "  +=\n").IsCorrect);
    }

    [Fact]
    public void A_regex_mistake_gives_feedback_and_other_wrong_answers_none()
    {
        Assert.Equal("range(n) starts at 0 and stops before n.", Evaluate("range(n)", "+=").Blanks[0].Feedback);
        Assert.Null(Evaluate("something else", "+=").Blanks[0].Feedback);
    }

    [Fact]
    public void An_empty_or_missing_blank_is_wrong_without_feedback()
    {
        var result = AnswerEvaluator.Evaluate(Step, new Dictionary<string, string> { ["iter"] = "   " });

        Assert.False(result.IsCorrect);
        Assert.All(result.Blanks, b => { Assert.False(b.IsCorrect); Assert.Null(b.Feedback); });
    }

    [Fact]
    public void The_template_splits_into_lines_of_code_and_blank_parts()
    {
        var lines = FillBlankTemplate.Lines(Step.Template);

        Assert.Equal(2, lines.Count);
        Assert.Equal([TemplatePart.Code("for i in "), TemplatePart.Blank("iter"), TemplatePart.Code(":")], lines[0]);
        Assert.Equal([TemplatePart.Code("    total "), TemplatePart.Blank("op"), TemplatePart.Code(" i")], lines[1]);
    }

    [Fact]
    public void Blank_lines_and_indentation_survive_and_stray_braces_stay_literal()
    {
        var lines = FillBlankTemplate.Lines("x = {{a}}\n\nd = {1: 2}\n");

        Assert.Equal(3, lines.Count);
        Assert.Empty(lines[1]);
        Assert.Equal([TemplatePart.Code("d = {1: 2}")], lines[2]);
    }

    [Fact]
    public void Markers_lists_every_double_brace_group_and_fill_substitutes_valid_ones()
    {
        Assert.Equal(["iter", "op", "Bad Id"], FillBlankTemplate.Markers("{{iter}} {{op}} {{Bad Id}}"));
        Assert.Equal("range +=", FillBlankTemplate.Fill("{{a}} {{b}}", id => id == "a" ? "range" : "+="));
        Assert.Equal("{{Bad Id}}", FillBlankTemplate.Fill("{{Bad Id}}", _ => "x"));
    }

    [Fact]
    public void A_fill_blank_step_survives_a_pack_round_trip()
    {
        var lesson = new Lesson("lesson.one", "One", "track.a", [Step with { Hints = ["nudge"] }]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            [new Track("track.a", "A", ["lesson.one"])], [lesson]);
        ms.Position = 0;

        var loaded = Assert.IsType<FillBlankStep>(ContentPackFormat.Load(ms).Get<Lesson>("lesson.one").Steps[0]);

        Assert.Equal(Step.Template, loaded.Template);
        Assert.Equal(["iter", "op"], loaded.Blanks.Select(b => b.Id));
        Assert.Equal(Step.Blanks[0].Accepted, loaded.Blanks[0].Accepted);
        Assert.Equal("A plain = replaces the total each time.", loaded.Blanks[1].Mistakes[0].Feedback);
        Assert.Equal(["nudge"], loaded.Hints);
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    [Fact]
    public void An_attempt_is_recorded_with_what_was_typed_in_each_blank()
    {
        var log = new MemoryLog();
        new ProgressRecorder(log, "d").StepAnswered("lesson.one", "step.fb", false, new Dictionary<string, string> { ["iter"] = "range(n)", ["op"] = "+=" });

        var evt = log.Items.Single();
        Assert.Equal(ProgressEventTypes.StepAnswered, evt.Type);
        using var doc = JsonDocument.Parse(evt.Data!);
        Assert.False(doc.RootElement.GetProperty("correct").GetBoolean());
        Assert.Equal("range(n)", doc.RootElement.GetProperty("blanks").GetProperty("iter").GetString());
    }
}
