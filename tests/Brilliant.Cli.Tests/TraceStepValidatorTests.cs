using Brilliant.Cli;
using Brilliant.Core.Content;
using Brilliant.Core.Python;

namespace Brilliant.Cli.Tests;

public sealed class TraceStepValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brilliant-trace-tests-" + Guid.NewGuid().ToString("N"));

    public TraceStepValidatorTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private const string Header = "id: lesson.one\ntitle: One\n";
    private const string Head = "steps:\n  - id: step.one.t\n    type: trace\n    title: Walk\n";
    private const string Body = "    body: Watch i.\n";
    private const string Code = "    code: |\n      nums = [3, 8, 2]\n      for i in range(3):\n          pass\n";
    private const string Visualise = "    visualise:\n      - variable: nums\n        as: array\n        pointers: [i]\n";

    private void WriteLesson(string yaml)
    {
        Write("pack.yaml", "id: pack.t\nversion: 1.0.0\n");
        Write("tracks/t/track.yaml", "id: track.t\ntitle: T\n");
        Write("tracks/t/l1/lesson.yaml", yaml);
    }

    private List<string> Errors(string stepYaml, IReferenceSolutionRunner? runner = null)
    {
        WriteLesson(Header + stepYaml);
        return ContentValidator.Validate(_root, runner ?? new FakeRunner(Good)).Errors.Select(e => e.Message).ToList();
    }

    // ---- Fake tracer ----

    private static TraceFrame Frame(int? line, params (string Name, string Type, string Repr)[] locals) =>
        new(line is null ? "return" : "line", line, "<module>", locals.Select(l => new TraceLocal(l.Name, l.Type, l.Repr)).ToList(),
            new Dictionary<string, TrackedArray> { ["nums"] = new("list", ["3", "8", "2"], 0) });

    private static TraceResult Good(string code, IReadOnlyList<Visual> visuals) =>
        new(TraceStatus.Ok, [Frame(1), Frame(2, ("i", "int", "0")), Frame(null, ("i", "int", "2"))], "", null, null, null);

    private sealed class FakeRunner(Func<string, IReadOnlyList<Visual>, TraceResult> trace) : IReferenceSolutionRunner
    {
        public List<(string Code, IReadOnlyList<Visual> Visuals)> Traced { get; } = [];
        public TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests) => throw new NotSupportedException();
        public TraceResult Trace(string code, IReadOnlyList<Visual> visuals) { Traced.Add((code, visuals)); return trace(code, visuals); }
    }

    // ---- Shape ----

    [Fact]
    public void A_trace_step_loads_into_the_pack_with_its_code_and_visuals()
    {
        WriteLesson(Header + Head + Body + Code + Visualise);
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath, new FakeRunner(Good));

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<TraceStep>(ContentPackFormat.Load(outPath).Get<Lesson>("lesson.one").Steps[0]);
        Assert.Equal("Watch i.", step.Body.Trim());
        Assert.StartsWith("nums = [3, 8, 2]", step.Code);
        var visual = Assert.Single(step.Visuals);
        Assert.Equal(("nums", "array"), (visual.Variable, visual.As));
        Assert.Equal(["i"], visual.Pointers);
    }

    [Fact]
    public void Pointers_are_optional()
    {
        Assert.Empty(Errors(Head + Body + Code + "    visualise:\n      - variable: nums\n        as: array\n"));
    }

    [Fact]
    public void Body_code_and_visualise_are_required()
    {
        var m = Errors(Head);

        Assert.Contains(m, x => x.Contains("'body' is required"));
        Assert.Contains(m, x => x.Contains("'code' is required"));
        Assert.Contains(m, x => x.Contains("'visualise'") && x.Contains("at least one"));
    }

    [Fact]
    public void An_empty_visualise_list_is_the_same_as_none()
    {
        Assert.Contains(Errors(Head + Body + Code + "    visualise: []\n"), x => x.Contains("at least one"));
    }

    [Fact]
    public void Code_is_required_even_when_the_rest_is_there()
    {
        Assert.Contains(Errors(Head + Body + Visualise), x => x.Contains("'code' is required"));
    }

    [Fact]
    public void A_visual_needs_a_variable_and_a_kind()
    {
        var m = Errors(Head + Body + Code + "    visualise:\n      - pointers: [i]\n");

        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains("'variable' is required"));
        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains("'as' is required"));
    }

    [Fact]
    public void Only_the_kinds_the_player_knows_can_be_drawn()
    {
        var m = Errors(Head + Body + Code + "    visualise:\n      - variable: nums\n        as: graph\n");

        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains("unsupported") && x.Contains("'graph'") && x.Contains("array, stack, queue, dict, set"));
    }

    [Fact]
    public void An_unknown_option_on_a_visual_is_rejected()
    {
        var m = Errors(Head + Body + Code + "    visualise:\n      - variable: nums\n        as: stack\n        top: right\n");

        Assert.NotEmpty(m);
    }

    [Theory]
    [InlineData("dict")]
    [InlineData("set")]
    public void Dicts_and_sets_can_be_drawn_with_pointers_naming_the_keys_to_look_up(string kind)
    {
        var yaml = Head + Body + Code + $"    visualise:\n      - variable: counts\n        as: {kind}\n        pointers: [word]\n";
        var runner = new FakeRunner((_, _) => TableTrace(kind));

        Assert.Empty(Errors(yaml, runner));
        var visual = Assert.Single(Assert.Single(runner.Traced).Visuals);
        Assert.Equal(("counts", kind), (visual.Variable, visual.As));
        Assert.Equal(["word"], visual.Pointers);
    }

    [Fact]
    public void A_dict_step_survives_packing()
    {
        WriteLesson(Header + Head + Body + Code + "    visualise:\n      - variable: counts\n        as: dict\n        pointers: [word]\n");
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath, new FakeRunner((_, _) => TableTrace("dict")));

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<TraceStep>(ContentPackFormat.Load(outPath).Get<Lesson>("lesson.one").Steps[0]);
        Assert.Equal(new Visual("counts", Visual.Dict, ["word"]), step.Visuals[0], new VisualComparer());
    }

    private sealed class VisualComparer : IEqualityComparer<Visual>
    {
        public bool Equals(Visual? a, Visual? b) =>
            a!.Variable == b!.Variable && a.As == b.As && a.Pointers.SequenceEqual(b.Pointers) && (a.Next, a.Value) == (b.Next, b.Value)
            && (a.Left, a.Right, a.Visited) == (b.Left, b.Right, b.Visited) && a.Cells.SequenceEqual(b.Cells)
            && (a.RowLabels, a.ColLabels, a.LabelOffset, a.Highlight) == (b.RowLabels, b.ColLabels, b.LabelOffset, b.Highlight);
        public int GetHashCode(Visual v) => v.Variable.GetHashCode();
    }

    [Theory]
    [InlineData("variable: 2nums", "variable")]
    [InlineData("variable: nums\n        pointers: [i, 'j k']", "pointer")]
    public void Names_must_be_python_identifiers(string fields, string what)
    {
        var m = Errors(Head + Body + Code + $"    visualise:\n      - as: array\n        {fields}\n");

        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains(what) && x.Contains("not a valid Python"));
    }

    [Fact]
    public void A_variable_is_drawn_once_and_a_pointer_named_once()
    {
        var m = Errors(Head + Body + Code
            + "    visualise:\n      - variable: nums\n        as: array\n        pointers: [i, i]\n      - variable: nums\n        as: array\n");

        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains("pointer 'i'") && x.Contains("twice"));
        Assert.Contains(m, x => x.Contains("visualise[1]") && x.Contains("'nums'") && x.Contains("already"));
    }

    [Fact]
    public void Trace_steps_take_no_hints()
    {
        var m = Errors(Head + Body + Code + Visualise + "    hints:\n      - a\n");

        Assert.Contains(m, x => x.Contains("'hints' only apply to questions"));
    }

    [Fact]
    public void Unknown_fields_are_rejected()
    {
        Assert.NotEmpty(Errors(Head + Body + Code + Visualise + "    bogus: 1\n"));
    }

    [Fact]
    public void A_trace_step_cannot_back_a_review_item()
    {
        var m = Errors("concepts:\n  - id: concept.loops\n    title: Loops\nreviewItems:\n  - id: review.one.t\n    concept: concept.loops\n    step: step.one.t\n"
            + Head + Body + Code + Visualise);

        Assert.Contains(m, x => x.Contains("review item needs a question") && x.Contains("trace"));
    }

    [Fact]
    public void The_unsupported_type_message_lists_trace()
    {
        var m = Errors("steps:\n  - id: step.one.t\n    type: video\n    title: V\n");

        Assert.Contains(m, x => x.Contains("unsupported step type 'video'") && x.Contains("trace"));
    }

    // ---- The code is really run ----

    [Fact]
    public void The_authored_code_is_traced_with_its_visuals_before_packing()
    {
        var runner = new FakeRunner(Good);

        Assert.Empty(Errors(Head + Body + Code + Visualise, runner));

        var traced = Assert.Single(runner.Traced);
        Assert.StartsWith("nums = [3, 8, 2]", traced.Code);
        Assert.Equal(["nums"], traced.Visuals.Select(v => v.Variable));
    }

    [Fact]
    public void Code_that_raises_is_reported_with_its_traceback()
    {
        var runner = new FakeRunner((_, _) => new(TraceStatus.Error, [Frame(1)], "", "The code raised an error.", "ZeroDivisionError: division by zero", 3));

        var m = Errors(Head + Body + Code + Visualise, runner);

        var message = Assert.Single(m);
        Assert.Contains("step.one.t", message);
        Assert.Contains("line 3", message);
        Assert.Contains("ZeroDivisionError", message);
    }

    [Fact]
    public void Code_that_runs_too_long_is_reported()
    {
        var runner = new FakeRunner((_, _) => new(TraceStatus.Truncated, [Frame(1)], "", "More than 300 steps.", null, null));

        var m = Errors(Head + Body + Code + Visualise, runner);

        Assert.Contains(m, x => x.Contains("too long") && x.Contains("More than 300 steps."));
    }

    [Fact]
    public void A_visualised_variable_that_is_never_a_list_is_reported()
    {
        var untracked = new TraceFrame("line", 1, "<module>", [], new Dictionary<string, TrackedArray>());
        var runner = new FakeRunner((_, _) => new(TraceStatus.Ok, [untracked], "", null, null, null));

        var m = Errors(Head + Body + Code + Visualise, runner);

        Assert.Contains(m, x => x.Contains("'nums'") && x.Contains("never a list"));
    }

    private static TraceResult SequenceTrace(string type) =>
        new(TraceStatus.Ok, [new TraceFrame("line", 1, "<module>", [new TraceLocal("i", "int", "0")],
            new Dictionary<string, TrackedArray> { ["nums"] = new(type, ["1"], 0) })], "", null, null, null);

    [Theory]
    [InlineData("stack", "list", true)]
    [InlineData("stack", "deque", true)]
    [InlineData("stack", "tuple", false)]
    [InlineData("queue", "list", true)]
    [InlineData("queue", "deque", true)]
    [InlineData("queue", "tuple", false)]
    [InlineData("array", "deque", true)]
    public void A_stack_or_queue_must_be_a_list_or_deque_somewhere_in_the_trace(string kind, string type, bool fine)
    {
        var yaml = Head + Body + Code + $"    visualise:\n      - variable: nums\n        as: {kind}\n        pointers: [i]\n";

        var m = Errors(yaml, new FakeRunner((_, _) => SequenceTrace(type)));

        if (fine) Assert.Empty(m);
        else Assert.Contains(m, x => x.Contains("step.one.t") && x.Contains("'nums'") && x.Contains("never a list or deque") && x.Contains($"no {kind}"));
    }

    [Theory]
    [InlineData("stack")]
    [InlineData("queue")]
    public void A_stack_or_queue_pointer_must_be_an_int_as_for_an_array(string kind)
    {
        var yaml = Head + Body + Code + $"    visualise:\n      - variable: nums\n        as: {kind}\n        pointers: [top]\n";

        Assert.Contains(Errors(yaml), x => x.Contains("pointer 'top'") && x.Contains("never an int"));
    }

    [Fact]
    public void A_pointer_that_is_never_an_int_is_reported()
    {
        var runner = new FakeRunner((_, _) => new(TraceStatus.Ok, [Frame(1, ("i", "str", "'a'")), Frame(null, ("i", "str", "'b'"))], "", null, null, null));

        var m = Errors(Head + Body + Code + Visualise, runner);

        Assert.Contains(m, x => x.Contains("pointer 'i'") && x.Contains("never an int"));
    }

    [Fact]
    public void A_trace_with_no_steps_is_reported()
    {
        var runner = new FakeRunner((_, _) => new(TraceStatus.Ok, [], "", null, null, null));

        Assert.Contains(Errors(Head + Body + Code + Visualise, runner), x => x.Contains("no steps"));
    }

    private static TraceResult TableTrace(string kind, string repr = "'a'", string pointerType = "str")
    {
        var table = kind == "dict" ? new TrackedTable("dict", "dict", [new TableRow("'a'", "1")], 0) : new TrackedTable("set", "set", [new TableRow("'a'", null)], 0);
        TraceFrame F(int? line) => new(line is null ? "return" : "line", line, "<module>", [new TraceLocal("word", pointerType, repr)],
            new Dictionary<string, TrackedArray>(), new Dictionary<string, TrackedTable> { ["counts"] = table });
        return new(TraceStatus.Ok, [F(1), F(null)], "", null, null, null);
    }

    [Theory]
    [InlineData("dict", "never a dict")]
    [InlineData("set", "never a set")]
    public void A_table_variable_that_is_never_that_kind_of_collection_is_reported(string kind, string expected)
    {
        var yaml = Head + Body + Code + $"    visualise:\n      - variable: counts\n        as: {kind}\n";
        var runner = new FakeRunner((_, _) => TableTrace(kind == "dict" ? "set" : "dict"));    // the other one

        Assert.Contains(Errors(yaml, runner), x => x.Contains("'counts'") && x.Contains(expected));
    }

    [Fact]
    public void A_dict_variable_that_is_a_list_in_the_trace_is_reported()
    {
        var yaml = Head + Body + Code + "    visualise:\n      - variable: nums\n        as: dict\n";

        Assert.Contains(Errors(yaml, new FakeRunner(Good)), x => x.Contains("'nums'") && x.Contains("never a dict"));
    }

    [Fact]
    public void A_lookup_pointer_that_is_never_a_variable_in_the_trace_is_reported()
    {
        var yaml = Head + Body + Code + "    visualise:\n      - variable: counts\n        as: dict\n        pointers: [ghost]\n";

        var m = Errors(yaml, new FakeRunner((_, _) => TableTrace("dict")));

        Assert.Contains(m, x => x.Contains("pointer 'ghost'") && x.Contains("never a variable"));
    }

    [Fact]
    public void A_lookup_pointer_may_hold_any_type_of_key()
    {
        var yaml = Head + Body + Code + "    visualise:\n      - variable: counts\n        as: dict\n        pointers: [word]\n";

        Assert.Empty(Errors(yaml, new FakeRunner((_, _) => TableTrace("dict", "3", "int"))));
    }

    [Fact]
    public void Code_with_no_executable_line_has_no_steps_to_trace()
    {
        var onlyTheModuleStart = new TraceFrame("line", 0, "<module>", [], new Dictionary<string, TrackedArray>());
        var done = new TraceFrame("return", null, "<module>", [], new Dictionary<string, TrackedArray>());
        var runner = new FakeRunner((_, _) => new(TraceStatus.Ok, [onlyTheModuleStart, done], "", null, null, null));

        Assert.Contains(Errors(Head + Body + Code + Visualise, runner), x => x.Contains("no steps"));
    }

    [Fact]
    public void Nothing_is_traced_when_the_content_is_otherwise_invalid()
    {
        var runner = new FakeRunner(Good);

        Assert.NotEmpty(Errors(Head + Visualise, runner));    // no body, no code
        Assert.Empty(runner.Traced);
    }

    [Fact]
    public void A_missing_python_is_reported_once_even_with_exercises_and_trace_steps_together()
    {
        var exercise = "  - id: step.one.w\n    type: write-code\n    title: W\n    prompt: Write it\n    language: python\n    entrypoint: add\n"
            + "    solution: |\n      def add(a, b):\n          return a + b\n    tests:\n      - input: '1, 2'\n        expected: '3'\n";
        WriteLesson(Header + Head + Body + Code + Visualise + exercise);

        var error = Assert.Single(ContentValidator.Validate(_root, new CPythonRunner(python: "definitely-not-a-python")).Errors);

        Assert.Contains("BRILLIANT_PYTHON", error.Message);
    }

    [Fact]
    public void A_missing_python_is_reported_once_with_how_to_fix_it()
    {
        WriteLesson(Header + Head + Body + Code + Visualise);

        var error = Assert.Single(ContentValidator.Validate(_root, new CPythonRunner(python: "definitely-not-a-python")).Errors);

        Assert.Contains("BRILLIANT_PYTHON", error.Message);
    }

    // ---- Real CPython, with the very tracer.py the app runs ----

    [Fact]
    public void Real_cpython_traces_the_sample()
    {
        Assert.Empty(Errors(Head + Body + Code + Visualise, new CPythonRunner()));
    }

    [Fact]
    public void Real_cpython_reports_code_that_raises()
    {
        var m = Errors(Head + Body + "    code: |\n      nums = [1]\n      nums[5]\n" + Visualise, new CPythonRunner());

        Assert.Contains(m, x => x.Contains("IndexError") && x.Contains("line 2"));
    }

    [Fact]
    public void Real_cpython_stops_code_that_never_ends()
    {
        var m = Errors(Head + Body + "    code: |\n      nums = [1]\n      while True:\n          pass\n" + Visualise, new CPythonRunner());

        Assert.Contains(m, x => x.Contains("too long"));
    }

    [Fact]
    public void Real_cpython_reports_a_visualised_name_the_code_never_makes()
    {
        var m = Errors(Head + Body + "    code: |\n      xs = [1]\n" + Visualise, new CPythonRunner());

        Assert.Contains(m, x => x.Contains("'nums'") && x.Contains("never a list"));
    }

    [Fact]
    public void Real_cpython_traces_a_dict_and_a_set_with_lookups()
    {
        const string code = "    code: |\n      counts = {}\n      seen = set()\n      for word in ['a', 'b', 'a']:\n          counts[word] = counts.get(word, 0) + 1\n          seen.add(word)\n";
        const string visuals = "    visualise:\n      - variable: counts\n        as: dict\n        pointers: [word]\n      - variable: seen\n        as: set\n        pointers: [word]\n";

        Assert.Empty(Errors(Head + Body + code + visuals, new CPythonRunner()));
    }

    [Fact]
    public void Real_cpython_traces_a_stack_and_a_deque_queue()
    {
        const string code = "    code: |\n      from collections import deque\n      stack = []\n      q = deque()\n      for x in [1, 2, 3]:\n          stack.append(x)\n          q.append(x)\n      top = len(stack) - 1\n      stack.pop()\n      q.popleft()\n";
        const string visuals = "    visualise:\n      - variable: stack\n        as: stack\n        pointers: [top]\n      - variable: q\n        as: queue\n";

        Assert.Empty(Errors(Head + Body + code + visuals, new CPythonRunner()));
    }

    [Fact]
    public void Real_cpython_reports_a_stack_that_is_really_a_tuple()
    {
        var m = Errors(Head + Body + "    code: |\n      s = (1, 2)\n    visualise:\n      - variable: s\n        as: stack\n", new CPythonRunner());

        Assert.Contains(m, x => x.Contains("'s'") && x.Contains("never a list or deque"));
    }

    [Fact]
    public void Real_cpython_reports_a_dict_that_is_really_a_list()
    {
        var m = Errors(Head + Body + "    code: |\n      counts = [1]\n    visualise:\n      - variable: counts\n        as: dict\n", new CPythonRunner());

        Assert.Contains(m, x => x.Contains("'counts'") && x.Contains("never a dict"));
    }

    [Fact]
    public void Real_cpython_reports_code_with_only_comments_as_having_no_steps()
    {
        var m = Errors(Head + Body + "    code: |\n      # nothing to run\n" + Visualise, new CPythonRunner());

        Assert.Contains(m, x => x.Contains("no steps"));
    }

    [Fact]
    public void Real_cpython_does_not_call_a_single_statement_empty()
    {
        var m = Errors(Head + Body + "    code: |\n      nums = [1]\n" + Visualise, new CPythonRunner());

        Assert.DoesNotContain(m, x => x.Contains("no steps"));
    }

    // ---- Linked lists ----

    private const string ListVisual = "    visualise:\n      - variable: head\n        as: linked_list\n";

    /// <summary>A trace in which head holds node 1 -> 2, with each named pointer holding a node (or None, for a null id).</summary>
    private static TraceResult ListTrace(int? head = 1, string? value = "1", params (string Name, int? Node)[] pointers)
    {
        var list = new TrackedList(head, head is null ? [] : [new ListNode(1, value, 2), new ListNode(2, value, null)], 0,
            pointers.ToDictionary(p => p.Name, p => p.Node));
        TraceFrame F(int? line) => new(line is null ? "return" : "line", line, "<module>", [], new Dictionary<string, TrackedArray>(), null,
            new Dictionary<string, TrackedList> { ["head"] = list });
        return new(TraceStatus.Ok, [F(1), F(null)], "", null, null, null);
    }

    [Fact]
    public void A_linked_list_loads_with_its_options_and_defaults()
    {
        WriteLesson(Header + Head + Body + Code + ListVisual + "        next: link\n        pointers: [curr]\n");
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath, new FakeRunner((_, _) => ListTrace(1, "1", ("curr", 2))));

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<TraceStep>(ContentPackFormat.Load(outPath).Get<Lesson>("lesson.one").Steps[0]);
        Assert.Equal(new Visual("head", Visual.LinkedList, ["curr"], Next: "link", Value: "val"), step.Visuals[0], new VisualComparer());
    }

    [Theory]
    [InlineData("next: link")]
    [InlineData("value: data")]
    public void Next_and_value_are_options_of_a_linked_list_only(string option)
    {
        var m = Errors(Head + Body + Code + $"    visualise:\n      - variable: nums\n        as: stack\n        {option}\n");

        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains($"'{option[..option.IndexOf(':')]}'") && x.Contains("linked_list")
            && x.Contains("only, not of 'stack'"));
    }

    [Fact]
    public void Next_and_value_must_be_attribute_names()
    {
        var m = Errors(Head + Body + Code + ListVisual + "        next: 'the next'\n        value: 2v\n");

        Assert.Contains(m, x => x.Contains("next 'the next'") && x.Contains("not a valid Python attribute"));
        Assert.Contains(m, x => x.Contains("value '2v'") && x.Contains("not a valid Python attribute"));
    }

    [Fact]
    public void A_linked_list_does_not_list_its_own_variable_as_a_pointer()
    {
        var m = Errors(Head + Body + Code + ListVisual + "        pointers: [head]\n", new FakeRunner((_, _) => ListTrace()));

        Assert.Contains(m, x => x.Contains("pointer 'head'") && x.Contains("own variable"));
    }

    [Fact]
    public void A_linked_list_whose_variable_never_holds_a_node_is_reported()
    {
        var m = Errors(Head + Body + Code + ListVisual + "        next: link\n", new FakeRunner((_, _) => ListTrace(head: null)));

        Assert.Contains(m, x => x.Contains("'head'") && x.Contains("never holds a node") && x.Contains("'link' attribute"));
    }

    [Fact]
    public void Nodes_without_the_value_attribute_are_reported()
    {
        var m = Errors(Head + Body + Code + ListVisual + "        value: data\n", new FakeRunner((_, _) => ListTrace(value: null)));

        Assert.Contains(m, x => x.Contains("no attribute 'data'") && x.Contains("set 'value'"));
    }

    [Fact]
    public void A_list_pointer_that_never_holds_a_node_is_reported_but_one_that_is_sometimes_none_is_fine()
    {
        var m = Errors(Head + Body + Code + ListVisual + "        pointers: [prev, curr, ghost]\n",
            new FakeRunner((_, _) => ListTrace(1, "1", ("prev", null), ("curr", 2))));

        Assert.Contains(m, x => x.Contains("pointer 'prev'") && x.Contains("never holds a node"));
        Assert.Contains(m, x => x.Contains("pointer 'ghost'") && x.Contains("never holds a node"));
        Assert.DoesNotContain(m, x => x.Contains("pointer 'curr'"));
    }

    private const string Reverse = "    code: |\n      class Node:\n          def __init__(self, val, next=None):\n              self.val = val\n              self.next = next\n"
        + "      head = Node(1, Node(2, Node(3)))\n      prev, curr = None, head\n      while curr:\n          nxt = curr.next\n"
        + "          curr.next = prev\n          prev, curr = curr, nxt\n      head = prev\n";

    [Fact]
    public void Real_cpython_traces_reversing_a_linked_list()
    {
        Assert.Empty(Errors(Head + Body + Reverse + ListVisual + "        pointers: [prev, curr, nxt]\n", new CPythonRunner()));
    }

    [Fact]
    public void Real_cpython_traces_dataclass_nodes_with_other_attribute_names()
    {
        const string code = "    code: |\n      from dataclasses import dataclass\n      @dataclass\n      class Item:\n          data: int\n          link: 'Item | None' = None\n"
            + "      head = Item(1, Item(2))\n      walk = head.link\n";

        Assert.Empty(Errors(Head + Body + code + ListVisual + "        next: link\n        value: data\n        pointers: [walk]\n", new CPythonRunner()));
    }

    [Fact]
    public void Real_cpython_reports_a_value_attribute_the_nodes_do_not_have()
    {
        var m = Errors(Head + Body + Reverse + ListVisual + "        value: data\n", new CPythonRunner());

        Assert.Contains(m, x => x.Contains("no attribute 'data'"));
    }

    [Fact]
    public void Real_cpython_reports_a_list_variable_that_is_never_a_node()
    {
        var m = Errors(Head + Body + Reverse + ListVisual + "        next: link\n", new CPythonRunner());

        Assert.Contains(m, x => x.Contains("'head'") && x.Contains("never holds a node"));
    }

    // ---- Trees ----

    private const string TreeVisual = "    visualise:\n      - variable: root\n        as: tree\n";

    /// <summary>
    /// A trace in which root holds node 1 with a left child 2, with each named pointer holding a node (or None, for a null id), and the
    /// visited variable a list marking the given nodes (null: not a list or set).
    /// </summary>
    private static TraceResult TreeTrace(int? root = 1, string? value = "1", int[]? visited = null, params (string Name, int? Node)[] pointers)
    {
        var tree = new TrackedTree(root, root is null ? [] : [new TreeNode(1, value, 2, null), new TreeNode(2, value, null, null)], 0,
            pointers.ToDictionary(p => p.Name, p => p.Node),
            visited is null ? null : new TreeVisited(true, visited.Select((id, i) => new VisitedNode(id, i + 1)).ToList(), 0));
        TraceFrame F(int? line) => new(line is null ? "return" : "line", line, "<module>", [], new Dictionary<string, TrackedArray>(),
            Trees: new Dictionary<string, TrackedTree> { ["root"] = tree });
        return new(TraceStatus.Ok, [F(1), F(null)], "", null, null, null);
    }

    [Fact]
    public void A_tree_loads_with_its_options_and_defaults()
    {
        WriteLesson(Header + Head + Body + Code + TreeVisual + "        right: hi\n        visited: order\n        pointers: [node]\n");
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath, new FakeRunner((_, _) => TreeTrace(1, "1", [2], ("node", 2))));

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<TraceStep>(ContentPackFormat.Load(outPath).Get<Lesson>("lesson.one").Steps[0]);
        Assert.Equal(new Visual("root", Visual.Tree, ["node"], Right: "hi", Visited: "order"), step.Visuals[0], new VisualComparer());
        Assert.Equal(("left", "val"), (step.Visuals[0].Left, step.Visuals[0].Value));
    }

    [Theory]
    [InlineData("left: lo", "tree only")]
    [InlineData("right: hi", "tree only")]
    [InlineData("visited: order", "tree only")]
    public void Left_right_and_visited_are_options_of_a_tree_only(string option, string says)
    {
        var m = Errors(Head + Body + Code + $"    visualise:\n      - variable: nums\n        as: array\n        {option}\n");

        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains($"'{option[..option.IndexOf(':')]}'") && x.Contains(says) && x.Contains("not of 'array'"));
    }

    [Fact]
    public void Next_is_not_an_option_of_a_tree_but_value_is()
    {
        var m = Errors(Head + Body + Code + TreeVisual + "        next: link\n        value: key\n", new FakeRunner((_, _) => TreeTrace()));

        Assert.Contains(m, x => x.Contains("'next'") && x.Contains("not of 'tree'"));
        Assert.DoesNotContain(m, x => x.Contains("'value'"));
    }

    [Fact]
    public void Tree_options_must_be_names_and_left_and_right_must_differ()
    {
        var m = Errors(Head + Body + Code + TreeVisual + "        left: 'l r'\n        visited: 9v\n", new FakeRunner((_, _) => TreeTrace()));
        Assert.Contains(m, x => x.Contains("left 'l r'") && x.Contains("not a valid Python attribute"));
        Assert.Contains(m, x => x.Contains("visited '9v'") && x.Contains("not a valid Python name"));

        var same = Errors(Head + Body + Code + TreeVisual + "        left: right\n", new FakeRunner((_, _) => TreeTrace()));
        Assert.Contains(same, x => x.Contains("'left' and 'right' name the same attribute"));
    }

    [Fact]
    public void A_tree_does_not_list_its_own_variable_as_a_pointer_or_as_visited()
    {
        var m = Errors(Head + Body + Code + TreeVisual + "        pointers: [root]\n        visited: root\n", new FakeRunner((_, _) => TreeTrace()));

        Assert.Contains(m, x => x.Contains("pointer 'root'") && x.Contains("own variable"));
        Assert.Contains(m, x => x.Contains("visited 'root'") && x.Contains("own variable"));
    }

    [Fact]
    public void A_tree_whose_variable_never_holds_a_node_is_reported()
    {
        var m = Errors(Head + Body + Code + TreeVisual + "        left: lo\n", new FakeRunner((_, _) => TreeTrace(root: null)));

        Assert.Contains(m, x => x.Contains("'root'") && x.Contains("never holds a tree node") && x.Contains("'lo' and 'right'"));
    }

    [Fact]
    public void Tree_nodes_without_the_value_attribute_are_reported()
    {
        var m = Errors(Head + Body + Code + TreeVisual + "        value: key\n", new FakeRunner((_, _) => TreeTrace(value: null)));

        Assert.Contains(m, x => x.Contains("no attribute 'key'") && x.Contains("set 'value'"));
    }

    [Fact]
    public void A_tree_pointer_that_never_holds_a_node_is_reported()
    {
        var m = Errors(Head + Body + Code + TreeVisual + "        pointers: [node, gone]\n", new FakeRunner((_, _) => TreeTrace(1, "1", null, ("node", 2), ("gone", null))));

        Assert.Contains(m, x => x.Contains("pointer 'gone'") && x.Contains("never holds a node of the tree"));
        Assert.DoesNotContain(m, x => x.Contains("pointer 'node'"));
    }

    [Fact]
    public void A_visited_variable_that_is_never_a_list_or_set_or_never_marks_a_node_is_reported()
    {
        var never = Errors(Head + Body + Code + TreeVisual + "        visited: order\n", new FakeRunner((_, _) => TreeTrace(visited: null)));
        Assert.Contains(never, x => x.Contains("visited 'order'") && x.Contains("never a list or set"));

        var empty = Errors(Head + Body + Code + TreeVisual + "        visited: order\n", new FakeRunner((_, _) => TreeTrace(visited: [])));
        Assert.Contains(empty, x => x.Contains("visited 'order'") && x.Contains("marks no node"));
    }

    private const string Bst = "    code: |\n      class TreeNode:\n          def __init__(self, val, left=None, right=None):\n"
        + "              self.val = val\n              self.left = left\n              self.right = right\n"
        + "      root = TreeNode(4, TreeNode(2, TreeNode(1), TreeNode(3)), TreeNode(6))\n"
        + "      order = []\n      def walk(node):\n          if node:\n              walk(node.left)\n              order.append(node.val)\n              walk(node.right)\n"
        + "      walk(root)\n";

    [Fact]
    public void Real_cpython_traces_an_in_order_walk_of_a_tree()
    {
        Assert.Empty(Errors(Head + Body + Bst + TreeVisual + "        visited: order\n", new CPythonRunner()));
    }

    [Fact]
    public void Real_cpython_reports_a_tree_variable_that_is_never_a_node()
    {
        var m = Errors(Head + Body + Bst + TreeVisual + "        left: lo\n", new CPythonRunner());

        Assert.Contains(m, x => x.Contains("'root'") && x.Contains("never holds a tree node"));
    }

    // ---- Grids ----

    private const string GridVisual = "    visualise:\n      - variable: dp\n        as: grid\n";

    /// <summary>
    /// A trace in which dp is a 2×2 grid (none: never a grid), each named pointer at its (row, col) (null: None), the labels recorded or
    /// not, and the highlight marking the given cells (null: not recorded).
    /// </summary>
    private static TraceResult GridTrace(bool grid = true, bool labels = true, int[][]? highlight = null, params (string Name, int[]? At)[] pointers)
    {
        var tracked = new TrackedGrid([["0", "0"], ["0", "0"]], [2, 2], 0,
            pointers.ToDictionary(p => p.Name, p => p.At is null ? null : (IReadOnlyList<int>?)p.At),
            labels ? ["a", "b"] : null, labels ? ["c", "d"] : null,
            highlight is null ? null : new GridHighlight(highlight, 0));
        TraceFrame F(int? line) => new(line is null ? "return" : "line", line, "<module>", [], new Dictionary<string, TrackedArray>(),
            Grids: grid ? new Dictionary<string, TrackedGrid> { ["dp"] = tracked } : null);
        return new(TraceStatus.Ok, [F(1), F(null)], "", null, null, null);
    }

    [Fact]
    public void A_grid_loads_with_its_options()
    {
        WriteLesson(Header + Head + Body + Code + GridVisual
            + "        cells: [[i, j]]\n        pointers: [cell]\n        rowLabels: a\n        colLabels: b\n        labelOffset: 1\n        highlight: seen\n");
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath, new FakeRunner((_, _) => GridTrace(true, true, [[0, 0]], ("i,j", [1, 1]), ("cell", [0, 1]))));

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<TraceStep>(ContentPackFormat.Load(outPath).Get<Lesson>("lesson.one").Steps[0]);
        Assert.Equal(new Visual("dp", Visual.Grid, ["cell"])
        {
            Cells = [new CellPair("i", "j")], RowLabels = "a", ColLabels = "b", LabelOffset = 1, Highlight = "seen",
        }, step.Visuals[0], new VisualComparer());
    }

    [Theory]
    [InlineData("cells: [[i, j]]", "'cells'")]
    [InlineData("rowLabels: a", "'rowLabels'")]
    [InlineData("colLabels: b", "'colLabels'")]
    [InlineData("highlight: seen", "'highlight'")]
    [InlineData("labelOffset: 1", "'labelOffset'")]
    public void Grid_options_belong_to_a_grid_only(string option, string says)
    {
        var m = Errors(Head + Body + Code + Visualise + "        " + option + "\n");

        Assert.Contains(m, x => x.Contains(says) && x.Contains("grid only") && x.Contains("not of 'array'"));
    }

    [Theory]
    [InlineData("cells: [[i]]", "a pair of int variables")]
    [InlineData("cells: [[i, j, k]]", "a pair of int variables")]
    [InlineData("cells: [[i, 'j k']]", "not a valid Python name")]
    [InlineData("cells: [[i, j], [i, j]]", "listed twice")]
    [InlineData("cells: [[dp, j]]", "the grid's own variable")]
    [InlineData("rowLabels: 9a", "not a valid Python name")]
    [InlineData("highlight: dp", "the grid's own variable")]
    [InlineData("pointers: [dp]", "the grid's own variable")]
    [InlineData("rowLabels: a\n        labelOffset: -1", "negative")]
    [InlineData("labelOffset: 1", "no labels")]
    public void Grid_options_are_checked(string option, string says)
    {
        var m = Errors(Head + Body + Code + GridVisual + "        " + option + "\n", new FakeRunner((_, _) => GridTrace()));

        Assert.Contains(m, x => x.Contains(says));
    }

    [Fact]
    public void A_grid_whose_variable_is_never_a_grid_is_reported()
    {
        var m = Errors(Head + Body + Code + GridVisual, new FakeRunner((_, _) => GridTrace(grid: false)));

        Assert.Contains(m, x => x.Contains("'dp'") && x.Contains("never a non-empty list of lists or strings"));
    }

    [Fact]
    public void Grid_pointers_that_never_land_on_a_cell_are_reported()
    {
        var m = Errors(Head + Body + Code + GridVisual + "        cells: [[i, j], [r, c]]\n        pointers: [cell, gone]\n",
            new FakeRunner((_, _) => GridTrace(true, true, null, ("i,j", [1, 1]), ("r,c", [2, 0]), ("cell", [0, 0]), ("gone", null))));

        Assert.Equal(2, m.Count);
        Assert.Contains(m, x => x.Contains("cells pair [r, c]") && x.Contains("never indexes a cell"));
        Assert.Contains(m, x => x.Contains("pointer 'gone'") && x.Contains("never holds a (row, col) pair"));
    }

    [Fact]
    public void Grid_labels_and_highlight_that_are_never_recorded_are_reported()
    {
        var never = Errors(Head + Body + Code + GridVisual + "        rowLabels: a\n        colLabels: b\n        highlight: seen\n",
            new FakeRunner((_, _) => GridTrace(labels: false)));
        var empty = Errors(Head + Body + Code + GridVisual + "        highlight: seen\n", new FakeRunner((_, _) => GridTrace(highlight: [])));

        Assert.Contains(never, x => x.Contains("rowLabels 'a'") && x.Contains("labels no row"));
        Assert.Contains(never, x => x.Contains("colLabels 'b'") && x.Contains("labels no column"));
        Assert.Contains(never, x => x.Contains("highlight 'seen'") && x.Contains("is never a list"));
        Assert.Contains(empty, x => x.Contains("highlight 'seen'") && x.Contains("never marks a cell"));
    }

    private const string Lcs = "    code: |\n      a, b = 'ab', 'cb'\n"
        + "      dp = [[0] * (len(b) + 1) for _ in range(len(a) + 1)]\n"
        + "      for i in range(1, len(a) + 1):\n          for j in range(1, len(b) + 1):\n"
        + "              dp[i][j] = dp[i - 1][j - 1] + 1 if a[i - 1] == b[j - 1] else max(dp[i - 1][j], dp[i][j - 1])\n";

    [Fact]
    public void Real_cpython_traces_a_dp_table_with_labels_and_a_cell_pair()
    {
        Assert.Empty(Errors(Head + Body + Lcs + GridVisual + "        cells: [[i, j]]\n        rowLabels: a\n        colLabels: b\n        labelOffset: 1\n",
            new CPythonRunner()));
    }

    [Fact]
    public void Real_cpython_reports_a_grid_variable_that_is_never_a_grid()
    {
        var m = Errors(Head + Body + Code + "    visualise:\n      - variable: nums\n        as: grid\n", new CPythonRunner());

        Assert.Contains(m, x => x.Contains("'nums'") && x.Contains("no grid to draw"));
    }

    // ---- Call stack ----

    private const string CallsVisual = "    visualise:\n      - as: call_stack\n";

    private const string Fact = "    code: |\n      def fact(n):\n          if n <= 1:\n              return 1\n          return n * fact(n - 1)\n\n      fact(3)\n";

    /// <summary>A trace in which the named functions are called, one after another, and return.</summary>
    private static TraceResult CallTrace(params string[] functions)
    {
        TraceFrame F(int? line, params RecordedCall[] calls) => new(line is null ? "return" : "line", line, "<module>", [],
            new Dictionary<string, TrackedArray>(), Calls: new TrackedCalls(calls, 0, null));
        return new(TraceStatus.Ok, [F(1), .. functions.Select((f, i) => F(2, new RecordedCall(i, f, [], null))), F(null)], "", null, null, null);
    }

    [Fact]
    public void A_call_stack_loads_with_its_functions_and_draws_no_variable()
    {
        WriteLesson(Header + Head + Body + Fact + CallsVisual + "        functions: [fact]\n");
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath, new FakeRunner((_, _) => CallTrace("fact")));

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var visual = Assert.IsType<TraceStep>(ContentPackFormat.Load(outPath).Get<Lesson>("lesson.one").Steps[0]).Visuals.Single();
        Assert.Equal(("", Visual.CallStack), (visual.Variable, visual.As));
        Assert.Equal(["fact"], visual.Functions);
    }

    [Fact]
    public void A_call_stack_sits_beside_other_visuals()
    {
        var m = Errors(Head + Body + Fact + "    visualise:\n      - as: call_stack\n      - variable: memo\n        as: dict\n",
            new FakeRunner((_, _) => CallTrace("fact")));

        Assert.DoesNotContain(m, x => x.Contains("visualise[1]") && x.Contains("already drawn"));
    }

    [Fact]
    public void A_call_stack_takes_no_variable_and_no_pointers_and_is_drawn_once()
    {
        var m = Errors(Head + Body + Fact + "    visualise:\n      - as: call_stack\n        variable: n\n        pointers: [n]\n      - as: call_stack\n",
            new FakeRunner((_, _) => CallTrace("fact")));

        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains("leave 'variable' out"));
        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains("has no pointers"));
        Assert.Contains(m, x => x.Contains("visualise[1]") && x.Contains("call stack is already drawn by visualise[0]"));
    }

    [Fact]
    public void Functions_is_an_option_of_a_call_stack_only()
    {
        var m = Errors(Head + Body + Code + "    visualise:\n      - variable: nums\n        as: array\n        functions: [f]\n");

        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains("'functions'") && x.Contains("call_stack only"));
    }

    [Fact]
    public void Functions_must_be_names_listed_once()
    {
        var m = Errors(Head + Body + Fact + CallsVisual + "        functions: [fact, fact, '2f']\n", new FakeRunner((_, _) => CallTrace("fact")));

        Assert.Contains(m, x => x.Contains("function 'fact' is listed twice"));
        Assert.Contains(m, x => x.Contains("function '2f'") && x.Contains("not a valid Python name"));
    }

    [Fact]
    public void A_call_stack_that_is_always_empty_is_reported()
    {
        Assert.Contains(Errors(Head + Body + Code + CallsVisual, new FakeRunner((_, _) => CallTrace())),
            x => x.Contains("never calls a function of its own"));
        Assert.Contains(Errors(Head + Body + Code + CallsVisual + "        functions: [fact]\n", new FakeRunner((_, _) => CallTrace())),
            x => x.Contains("never calls 'fact'"));
    }

    [Fact]
    public void A_listed_function_that_is_never_called_is_reported()
    {
        var m = Errors(Head + Body + Fact + CallsVisual + "        functions: [fact, helper]\n", new FakeRunner((_, _) => CallTrace("fact")));

        Assert.Contains(m, x => x.Contains("function 'helper'") && x.Contains("never called"));
        Assert.DoesNotContain(m, x => x.Contains("function 'fact'"));
    }

    [Fact]
    public void Real_cpython_traces_a_recursive_call_stack()
    {
        Assert.Empty(Errors(Head + Body + Fact + CallsVisual + "        functions: [fact]\n", new CPythonRunner()));
    }

    [Fact]
    public void Real_cpython_reports_a_call_stack_for_code_that_calls_nothing_of_its_own()
    {
        var m = Errors(Head + Body + "    code: |\n      print(len([1, 2]))\n" + CallsVisual, new CPythonRunner());

        Assert.Contains(m, x => x.Contains("never calls a function of its own"));
    }
}
