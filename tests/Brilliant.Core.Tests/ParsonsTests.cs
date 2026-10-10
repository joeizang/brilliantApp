using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Tests;

public class ParsonsTests
{
    private static readonly ParsonsStep Step = new("step.p", "Grade", "Order the lines", "python",
    [
        new("def grade(score):", 0),
        new("if score >= 50:", 1),
        new("return \"pass\"", 2),
        new("else:", 1),
        new("return \"fail\"", 2),
    ]);

    private static ParsonsBoard Solved()
    {
        var board = new ParsonsBoard(Step, [0, 1, 2, 3, 4]);
        for (var i = 0; i < Step.Lines.Count; i++) board.Indent(i, Step.Lines[i].Level);
        return board;
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    [Fact]
    public void Parsons_step_survives_a_pack_round_trip()
    {
        var lesson = new Lesson("lesson.one", "One", "track.a", [Step]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            [new Track("track.a", "A", ["lesson.one"])], [lesson]);
        ms.Position = 0;

        var loaded = Assert.IsType<ParsonsStep>(ContentPackFormat.Load(ms).Get<Lesson>("lesson.one").Steps[0]);

        Assert.Equal(Step.Lines, loaded.Lines);
        Assert.Equal("python", loaded.Language);
    }

    [Fact]
    public void The_right_lines_in_the_right_order_and_indentation_are_correct()
    {
        var result = Solved().Check();
        Assert.True(result.IsCorrect);
        Assert.Equal(5, result.CorrectCount);
    }

    [Fact]
    public void Right_order_with_flat_indentation_marks_the_indented_rows_as_wrong_indent()
    {
        var result = new ParsonsBoard(Step, [0, 1, 2, 3, 4]).Check();

        Assert.False(result.IsCorrect);
        Assert.Equal([RowVerdict.Correct, RowVerdict.WrongIndent, RowVerdict.WrongIndent, RowVerdict.WrongIndent, RowVerdict.WrongIndent], result.Rows);
    }

    [Fact]
    public void A_line_in_the_wrong_place_is_a_wrong_line()
    {
        var board = Solved();
        board.Move(2, 4);   // def, if, else, return fail, return pass  (indentation follows the row, not the line)

        var rows = board.Check().Rows;

        Assert.Equal(RowVerdict.Correct, rows[0]);
        Assert.Equal(RowVerdict.Correct, rows[1]);
        Assert.Equal(RowVerdict.WrongLine, rows[2]);
    }

    [Fact]
    public void Identical_lines_are_interchangeable()
    {
        var twin = new ParsonsStep("step.t", "T", "P", "python",
            [new("for i in range(3):", 0), new("print(i)", 1), new("print(i)", 1)]);
        var board = new ParsonsBoard(twin, [0, 2, 1]);
        board.Indent(1, 1);
        board.Indent(2, 1);

        Assert.True(board.Check().IsCorrect);
    }

    [Fact]
    public void Evaluating_a_malformed_arrangement_throws()
    {
        Assert.Throws<ArgumentException>(() => ParsonsEvaluator.Evaluate(Step, [new(0, 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ParsonsEvaluator.Evaluate(Step, [new(0, 0), new(1, 0), new(2, 0), new(3, 0), new(9, 0)]));
    }

    [Fact]
    public void A_board_must_hold_every_line_exactly_once()
    {
        Assert.Throws<ArgumentException>(() => new ParsonsBoard(Step, [0, 1, 2, 3]));
        Assert.Throws<ArgumentException>(() => new ParsonsBoard(Step, [0, 1, 2, 3, 3]));
        Assert.Throws<ArgumentException>(() => new ParsonsBoard(Step, [0, 1, 2, 3, 7]));
    }

    [Fact]
    public void A_shuffled_board_starts_flat_and_out_of_order()
    {
        for (var seed = 0; seed < 25; seed++)
        {
            var board = ParsonsBoard.Shuffled(Step, new Random(seed));

            Assert.Equal([0, 1, 2, 3, 4], board.Rows.Select(r => r.Piece).Order());
            Assert.All(board.Rows, r => Assert.Equal(0, r.Level));
            Assert.NotEqual([0, 1, 2, 3, 4], board.Rows.Select(r => r.Piece));
        }
    }

    [Fact]
    public void Move_puts_the_line_at_the_target_index()
    {
        var board = new ParsonsBoard(Step, [0, 1, 2, 3, 4]);

        Assert.True(board.Move(0, 2));
        Assert.Equal([1, 2, 0, 3, 4], board.Rows.Select(r => r.Piece));
        Assert.True(board.Move(4, 0));
        Assert.Equal([4, 1, 2, 0, 3], board.Rows.Select(r => r.Piece));
    }

    [Fact]
    public void Move_ignores_no_ops_and_out_of_range_targets()
    {
        var board = new ParsonsBoard(Step, [0, 1, 2, 3, 4]);

        Assert.False(board.Move(2, 2));
        Assert.False(board.Move(-1, 2));
        Assert.False(board.Move(2, 5));
        Assert.Equal([0, 1, 2, 3, 4], board.Rows.Select(r => r.Piece));
    }

    [Fact]
    public void Indent_is_clamped_between_zero_and_the_deepest_level_in_the_solution()
    {
        var board = new ParsonsBoard(Step, [0, 1, 2, 3, 4]);

        Assert.False(board.Indent(0, -1));
        Assert.True(board.Indent(0, 1));
        Assert.True(board.Indent(0, 1));
        Assert.False(board.Indent(0, 1));
        Assert.Equal(2, board.Rows[0].Level);
        Assert.Equal(2, board.MaxLevel);
        Assert.False(board.Indent(9, 1));
    }

    [Fact]
    public void Moving_a_line_keeps_its_indentation()
    {
        var board = new ParsonsBoard(Step, [0, 1, 2, 3, 4]);
        board.Indent(2, 2);

        board.Move(2, 0);

        Assert.Equal(new ParsonsPlacement(2, 2), board.Rows[0]);
    }

    [Fact]
    public void An_arrangement_is_recorded_with_the_verdict_and_every_line_and_level()
    {
        var log = new MemoryLog();
        var board = new ParsonsBoard(Step, [0, 2, 1, 3, 4]);

        new ProgressRecorder(log, "device").StepAnswered("lesson.one", "step.p", false, board.Rows);

        var evt = log.Items.Single();
        Assert.Equal(ProgressEventTypes.StepAnswered, evt.Type);
        using var doc = JsonDocument.Parse(evt.Data!);
        Assert.False(doc.RootElement.GetProperty("correct").GetBoolean());
        Assert.Equal([0, 2, 1, 3, 4], doc.RootElement.GetProperty("arrangement").EnumerateArray().Select(e => e.GetProperty("line").GetInt32()));
        Assert.All(doc.RootElement.GetProperty("arrangement").EnumerateArray(), e => Assert.Equal(0, e.GetProperty("level").GetInt32()));
    }
}
