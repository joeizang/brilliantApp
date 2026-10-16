using System.Runtime.CompilerServices;
using Brilliant.Core.Code;
using Brilliant.Core.Content;
using Brilliant.Runtime.CSharp;

namespace Brilliant.Runtime.CSharp.Tests;

public sealed class TraceRecorderTests
{
    private static string TracesDirectory([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Traces");

    private static readonly IReadOnlyDictionary<string, Visual[]> Watched = new Dictionary<string, Visual[]>
    {
        ["array-scan"] = [new("nums", Visual.Array, ["i"])],
        ["stack-and-queue"] = [new("stack", Visual.Stack, []), new("queue", Visual.Queue, [])],
        ["list-of-doubles"] = [new("values", Visual.Array, [])],
    };

    public static TheoryData<string> Fixtures() =>
        new(Directory.GetFiles(TracesDirectory(), "*.snippet").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).ToArray());

    private static TraceResult Record(string name)
    {
        var code = File.ReadAllText(Path.Combine(TracesDirectory(), name + ".snippet"));
        return CSharpTraceRecorder.Record(code, Watched.GetValueOrDefault(name, []));
    }

    // Golden files: a change to what is recorded is a change to every pack, so it shows up as a diff here.
    // Rewrite them with UPDATE_GOLDENS=1 after reading the diff.
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A_recording_matches_its_golden_file(string name)
    {
        var json = TraceResultJson.Serialize(Record(name)).ReplaceLineEndings("\n") + "\n";
        var golden = Path.Combine(TracesDirectory(), name + ".trace.json");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1") File.WriteAllText(golden, json);
        Assert.True(File.Exists(golden), $"No golden file for {name}; run with UPDATE_GOLDENS=1 to write it.");
        Assert.Equal(File.ReadAllText(golden).ReplaceLineEndings("\n"), json);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Recording_the_same_source_again_gives_the_same_text(string name)
    {
        Assert.Equal(TraceResultJson.Serialize(Record(name)), TraceResultJson.Serialize(Record(name)));
    }

    [Fact]
    public void Strings_and_chars_are_shown_as_c_literals_without_json_escapes()
    {
        var last = Record("text").Frames[^1];
        var reprs = last.Locals.ToDictionary(l => l.Name, l => l.Repr);
        Assert.Equal("\"it's <b> a+b & café\"", reprs["word"]);
        Assert.Equal("'é'", reprs["letter"]);
        Assert.Equal("'\\''", reprs["quote"]);
        Assert.Equal("[\"<li>\", \"naïve\", \"tab\\there\"]", reprs["words"]);
    }

    [Fact]
    public void A_stack_deeper_than_the_cap_keeps_its_bottom_items()
    {
        var result = CSharpTraceRecorder.Record("var stack = new Stack<int>();\nfor (var i = 0; i < 200; i++) stack.Push(i);\nvar done = true;", [new("stack", Visual.Stack, [])]);
        var cells = result.Frames[^1].Tracked!["stack"].Cells;
        Assert.Equal("0", cells[0]);
        Assert.Equal(200, result.Frames[^1].Tracked!["stack"].Cells.Count + result.Frames[^1].Tracked!["stack"].More);
    }

    [Fact]
    public void A_recording_survives_the_json_a_pack_stores()
    {
        var result = Record("array-scan");
        Assert.Equal(TraceResultJson.Serialize(result), TraceResultJson.Serialize(TraceResultJson.Parse(TraceResultJson.Serialize(result))));
    }

    [Fact]
    public void The_run_ends_with_a_return_frame_holding_the_final_variables()
    {
        var result = Record("counter-loop");
        Assert.Equal(TraceStatus.Ok, result.Status);
        var last = result.Frames[^1];
        Assert.Equal("return", last.Event);
        Assert.Null(last.Line);
        Assert.Equal("3", last.Locals.Single(l => l.Name == "total").Repr);
        Assert.Equal("3", result.Stdout.Trim());
    }

    [Fact]
    public void A_line_frame_is_the_state_before_that_line_with_the_learners_line_number()
    {
        var result = Record("counter-loop");
        var first = result.Frames[0];
        Assert.Equal("line", first.Event);
        Assert.Equal(1, first.Line);
        Assert.Empty(first.Locals);
        Assert.Equal(2, result.Frames[1].Line);
        Assert.Equal("0", result.Frames[1].Locals.Single().Repr);
    }

    [Fact]
    public void Integer_variables_carry_their_value_so_pointers_can_be_drawn()
    {
        var result = Record("array-scan");
        var frame = result.Frames.First(f => f.Locals.Any(l => l.Name == "i"));
        var i = frame.Locals.Single(l => l.Name == "i");
        Assert.Equal("int", i.Type);
        Assert.Equal(1, i.IntegerValue());
    }

    [Fact]
    public void A_watched_array_is_tracked_with_its_cells_and_csharp_type_name()
    {
        var result = Record("array-scan");
        var tracked = result.Frames[^1].Tracked["nums"];
        Assert.Equal("int[]", tracked.Type);
        Assert.Equal(["4", "1", "7"], tracked.Cells);
        Assert.True(tracked.Draws(Visual.Array));
        Assert.False(tracked.Draws(Visual.Stack));
        Assert.Equal("int[]", result.Frames[^1].Locals.Single(l => l.Name == "nums").Type);
    }

    [Fact]
    public void A_stack_is_drawn_bottom_to_top_and_a_queue_front_to_back()
    {
        var last = Record("stack-and-queue").Frames[^1];
        Assert.Equal(["1"], last.Tracked["stack"].Cells);
        Assert.Equal(["\"b\""], last.Tracked["queue"].Cells);
        var middle = Record("stack-and-queue").Frames.First(f => f.Line == 7);
        Assert.Equal(["1", "2"], middle.Tracked["stack"].Cells);
        Assert.Equal(["\"a\"", "\"b\""], middle.Tracked["queue"].Cells);
        Assert.True(middle.Tracked["stack"].Draws(Visual.Stack));
        Assert.True(middle.Tracked["queue"].Draws(Visual.Queue));
        Assert.Equal("Stack<int>", middle.Tracked["stack"].Type);
    }

    [Fact]
    public void A_list_is_drawn_as_an_array_and_values_read_as_csharp_literals()
    {
        var last = Record("list-of-doubles").Frames[^1];
        Assert.Equal(["1.5", "2.25", "3"], last.Tracked["values"].Cells);
        Assert.Equal("List<double>", last.Tracked["values"].Type);
        Assert.Equal("\"hi\"", last.Locals.Single(l => l.Name == "text").Repr);
        Assert.Equal("string", last.Locals.Single(l => l.Name == "text").Type);
    }

    [Fact]
    public void A_variable_that_is_not_watched_is_not_tracked()
    {
        var result = CSharpTraceRecorder.Record("int[] a = { 1 };\nvar b = a.Length;", []);
        Assert.Empty(result.Frames[^1].Tracked);
    }

    [Fact]
    public void Each_frame_names_the_method_it_is_in_and_locals_come_with_their_values()
    {
        var result = Record("recursion");
        Assert.Equal("Main", result.Frames[0].Function);
        var inFact = result.Frames.Where(f => f.Function == "Fact").ToArray();
        Assert.NotEmpty(inFact);
        Assert.Contains(inFact, f => f.Locals.Any(l => l is { Name: "n", Repr: "1" }));
        Assert.Equal("Main", result.Frames[^1].Function);
        Assert.Equal("6", result.Frames[^1].Locals.Single(l => l.Name == "answer").Repr);
    }

    [Fact]
    public void A_user_type_shows_its_fields()
    {
        var last = Record("objects").Frames[^1];
        Assert.Equal("Point", last.Locals.Single(l => l.Name == "p").Type);
        Assert.Equal("Point { X = 5, Y = 2 }", last.Locals.Single(l => l.Name == "p").Repr);
    }

    [Fact]
    public void An_exception_is_an_error_that_keeps_the_frames_up_to_it_and_the_line()
    {
        var result = Record("fails");
        Assert.Equal(TraceStatus.Error, result.Status);
        Assert.Contains("IndexOutOfRangeException", result.Message);
        Assert.Equal(4, result.ErrorLine);
        Assert.Equal("before", result.Stdout.Trim());
        Assert.Equal(4, result.Frames[^1].Line);
        Assert.DoesNotContain(result.Frames, f => f.Event == "return");
    }

    [Fact]
    public void Code_that_does_not_compile_is_an_error_on_its_line()
    {
        var result = CSharpTraceRecorder.Record("int a = 1;\nint b = \"two\";", []);
        Assert.Equal(TraceStatus.Error, result.Status);
        Assert.Equal(2, result.ErrorLine);
        Assert.Contains("CS0029", result.Message);
        Assert.Empty(result.Frames);
    }

    [Fact]
    public void A_run_longer_than_the_step_limit_is_truncated_not_hung()
    {
        var result = CSharpTraceRecorder.Record("int n = 0;\nwhile (true) { n++; }", []);
        Assert.Equal(TraceStatus.Truncated, result.Status);
        Assert.NotEmpty(result.Frames);
    }

    [Fact]
    public void A_run_with_more_steps_than_the_player_shows_is_truncated_at_the_frame_limit()
    {
        var result = CSharpTraceRecorder.Record("int n = 0;\nfor (int i = 0; i < 1000; i++) { n += i; }", []);
        Assert.Equal(TraceStatus.Truncated, result.Status);
        Assert.Equal(CSharpTraceRecorder.MaxFrames, result.Frames.Count);
    }

    [Fact]
    public void Unsupported_syntax_is_refused_with_a_reason()
    {
        var result = CSharpTraceRecorder.Record("async System.Threading.Tasks.Task F() { await System.Threading.Tasks.Task.Yield(); }", []);
        Assert.Equal(TraceStatus.Error, result.Status);
        Assert.Contains("async", result.Message);
    }
}
