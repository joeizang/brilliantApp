using Brilliant.Core.Code;
using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

/// <summary>A C# trace step or walkthrough is run when the pack is built; the pack carries what it recorded (#153).</summary>
public sealed class CSharpRecordedTraceTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "brilliant-csharp-recorded-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_repository, "content", "sample");
    private const string LessonFile = "tracks/one/scan/lesson.yaml";

    private void WritePack(string traceCode = "int[] nums = { 4, 1, 7 };\nint best = nums[0];\nint i = 1;\nwhile (i < nums.Length)\n{\n    if (nums[i] > best) best = nums[i];\n    i++;\n}",
        string traceVisuals = "[{variable: nums, as: array, pointers: [i]}]")
    {
        Write("apps/sample/manifest.yaml", """
            appId: example.csharp.recorded
            displayName: Recorded C# traces
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: sample.pack.zip
            runtimes: [csharp]
            stepTypes: [write-code, fill-blank, trace]
            """);
        Write("content/sample/pack.yaml", "id: pack.csharp.recorded\nversion: 1.0.0\ntargetFramework: net10.0\nlangVersion: '14'\n");
        Write("content/sample/tracks/one/track.yaml", "id: track.csharp.recorded\ntitle: C# recorded\n");
        var code = string.Join('\n', traceCode.Split('\n').Select(line => "      " + line));
        Write("content/sample/" + LessonFile, $$"""
            id: lesson.csharp.scan
            title: Scan an array
            steps:
              - id: step.csharp.scan.trace
                type: trace
                title: Find the largest
                body: Watch best change as i walks the array.
                language: csharp
                visualise: {{traceVisuals}}
                code: |
            {{code}}
              - id: step.csharp.double
                type: write-code
                title: Double
                prompt: Return twice the argument.
                language: csharp
                largeScreenPreferred: true
                entrypoint: Double
                starter: 'static int Double(int n) => 0;'
                solution: 'static int Double(int n) => n * 2;'
                tests:
                  - input: '4'
                    expected: '8'
                walkthrough:
                  explanation: Double returns n * 2.
                  call: 'var doubled = Double(4);'
                  visualise: []
                phoneAlternative:
                  id: step.csharp.double.fill
                  type: fill-blank
                  title: Complete Double
                  prompt: Finish Double.
                  language: csharp
                  template: 'static int Double(int n) => n * {{"{{factor}}"}};'
                  blanks:
                    - id: factor
                      accepted: ['2']
            """ + "\n");
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() { try { Directory.Delete(_repository, true); } catch (IOException) { } }

    private ContentGraph PackAndLoad()
    {
        var bytes = ContentPacker.PackToBytes(Root, out var report);
        Assert.True(report.IsValid, string.Join('\n', report.Errors));
        return ContentPackFormat.Load(new MemoryStream(bytes!));
    }

    [Fact]
    public void A_csharp_trace_step_carries_its_recording_in_the_pack()
    {
        WritePack();

        var lesson = PackAndLoad().Lessons.Single();

        var step = Assert.IsType<TraceStep>(lesson.Steps[0]);
        var recorded = Assert.IsType<TraceResult>(step.Recorded);
        Assert.Equal(TraceStatus.Ok, recorded.Status);
        Assert.Equal("return", recorded.Frames[^1].Event);
        Assert.Equal(["4", "1", "7"], recorded.Frames[^1].Tracked["nums"].Cells);
        Assert.Equal("int[]", recorded.Frames[^1].Tracked["nums"].Type);
    }

    [Fact]
    public void A_csharp_walkthrough_carries_its_recording_in_the_pack()
    {
        WritePack();

        var write = Assert.IsType<WriteCodeStep>(PackAndLoad().Lessons.Single().Steps[1]);

        var recorded = Assert.IsType<TraceResult>(write.Walkthrough!.Recorded);
        Assert.Equal("8", recorded.Frames[^1].Locals.Single(l => l.Name == "doubled").Repr);
        Assert.Contains(recorded.Frames, frame => frame.Function == "Double");
    }

    [Fact]
    public void A_recording_plays_in_the_trace_model_with_the_pointer_over_the_array()
    {
        WritePack();
        var step = Assert.IsType<TraceStep>(PackAndLoad().Lessons.Single().Steps[0]);

        var states = TraceModel.Build(step.Recorded!, step.Visuals);

        Assert.True(states.Count > 5);
        Assert.True(states[^1].IsFinal);
        Assert.NotEmpty(states[^1].Arrays);
    }

    [Fact]
    public void Packing_the_same_content_twice_gives_the_same_bytes()
    {
        WritePack();

        var first = ContentPacker.PackToBytes(Root, out var firstReport);
        var second = ContentPacker.PackToBytes(Root, out var secondReport);

        Assert.True(firstReport.IsValid && secondReport.IsValid, string.Join('\n', firstReport.Errors.Concat(secondReport.Errors)));
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_trace_that_does_not_compile_blocks_the_pack_with_its_line()
    {
        WritePack("int a = 1;\nint b = \"two\";", "[{variable: a, as: array}]");

        var report = ContentPacker.Pack(Root, Path.Combine(_repository, "bad.zip"));

        Assert.False(report.IsValid);
        var error = Assert.Single(report.Errors);
        Assert.Contains("step.csharp.scan.trace", error.Message);
        Assert.Contains("fails on line 2", error.Message);
        Assert.Contains("CS0029", error.Message);
    }

    [Fact]
    public void A_watched_variable_that_is_never_an_array_is_reported_in_csharp_terms()
    {
        WritePack("int total = 0;\ntotal += 1;");

        var report = ContentPacker.Pack(Root, Path.Combine(_repository, "never.zip"));

        var error = Assert.Single(report.Errors);
        Assert.Contains("'nums' is never an array, List<T>, Stack<T> or Queue<T>", error.Message);
    }

    [Fact]
    public void A_drawing_not_recorded_for_csharp_yet_is_refused_rather_than_drawn_empty()
    {
        WritePack("var seen = new HashSet<int> { 1 };", "[{variable: seen, as: graph}]");

        var report = ContentPacker.Pack(Root, Path.Combine(_repository, "graph.zip"));

        var error = Assert.Single(report.Errors);
        Assert.Contains("'graph' drawings of C# code are not recorded yet", error.Message);
    }

    [Fact]
    public void A_set_dictionary_heap_and_grid_are_recorded_into_the_pack()
    {
        WritePack("var seen = new HashSet<int> { 1, 2 };\nvar ages = new Dictionary<string, int> { [\"a\"] = 3 };\nvar heap = new List<int> { 1, 5, 2 };\nvar grid = new[] { new[] { 1, 2 }, new[] { 3, 4 } };",
            "[{variable: seen, as: set}, {variable: ages, as: dict}, {variable: heap, as: heap}, {variable: grid, as: grid}]");

        var step = Assert.IsType<TraceStep>(PackAndLoad().Lessons.Single().Steps[0]);

        var last = step.Recorded!.Frames[^1];
        Assert.Equal(2, last.Tables!["seen"].Rows.Count);
        Assert.Equal("3", last.Tables["ages"].Rows.Single().Value);
        Assert.Equal(["1", "5", "2"], last.Heaps!["heap"].Cells);
        Assert.Equal(2, last.Grids!["grid"].Rows.Count);
    }

    [Fact]
    public void A_linked_list_and_a_tree_are_recorded_into_the_pack()
    {
        WritePack("var head = new Node { Val = 1, Next = new Node { Val = 2 } };\nvar root = new Node { Val = 5, Left = new Node { Val = 3 } };\nvar done = true;\n\nclass Node\n{\n    public int Val;\n    public Node? Next, Left, Right;\n}",
            "[{variable: head, as: linked_list}, {variable: root, as: tree}]");

        var step = Assert.IsType<TraceStep>(PackAndLoad().Lessons.Single().Steps[0]);

        var last = step.Recorded!.Frames[^1];
        Assert.Equal(["1", "2"], last.Lists!["head"].Nodes.Select(n => n.Value));
        Assert.Equal(["5", "3"], last.Trees!["root"].Nodes.Select(n => n.Value));
    }

    [Fact]
    public void A_node_that_lacks_the_value_field_is_reported()
    {
        WritePack("var head = new Node { Next = new Node() };\n\nclass Node\n{\n    public Node? Next;\n}", "[{variable: head, as: linked_list}]");

        var report = ContentPacker.Pack(Root, Path.Combine(_repository, "novalue.zip"));

        Assert.Contains("have no attribute 'val'", Assert.Single(report.Errors).Message);
    }

    [Fact]
    public void A_dictionary_that_is_never_one_is_reported_in_csharp_terms()
    {
        WritePack("int total = 0;\ntotal += 1;", "[{variable: nums, as: dict}]");

        var report = ContentPacker.Pack(Root, Path.Combine(_repository, "nodict.zip"));

        Assert.Contains("'nums' is never a Dictionary<,>", Assert.Single(report.Errors).Message);
    }
}
