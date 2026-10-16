using System.Diagnostics;
using System.Reflection;
using System.Text;
using Brilliant.Core.Content;
using Brilliant.Core.Code;

namespace Brilliant.Cli;

/// <summary>A write-code exercise's reference solution, waiting to be proved against the step's own hidden tests.</summary>
internal sealed record ReferenceCheck(string File, string Label, string StepId, string Entrypoint, string Solution, IReadOnlyList<CodeTest> Tests, string Language = "python", IReadOnlyList<CodeFile>? Files = null);

/// <summary>A trace step's code, waiting to be run to prove it works and draws the variables the lesson says it does.</summary>
internal sealed record TraceCheck(string File, string Label, string Code, IReadOnlyList<Visual> Visuals, string Language = "python");

/// <summary>Runs source in one language against an exercise's tests. Production adapters use CPython or the C# compiler gate; tests may substitute a fake.</summary>
public interface IReferenceSolutionRunner
{
    /// <summary>The language accepted by this runner. Existing CPython adapters default to Python.</summary>
    string Language => "python";

    /// <exception cref="ReferenceRunnerUnavailableException">Python couldn't be started at all.</exception>
    TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests);

    /// <summary>
    /// The same for a multi-file exercise, whose files are compiled together. A runner with no notion of files answers a single file as
    /// <see cref="Run(string, string, IReadOnlyList{CodeTest})"/> and refuses more.
    /// </summary>
    TestRunResult Run(IReadOnlyList<CodeFile> files, string entrypoint, IReadOnlyList<CodeTest> tests) =>
        files.Count == 1
            ? Run(files[0].Content, entrypoint, tests)
            : new TestRunResult(TestRunStatus.Error, "", $"The {Language} checker runs a single file.", null, null, []);

    /// <summary>Records the execution of a trace step's code, as the app does.</summary>
    /// <exception cref="ReferenceRunnerUnavailableException">Python couldn't be started at all.</exception>
    TraceResult Trace(string code, IReadOnlyList<Visual> visuals);
}

public sealed class ReferenceRunnerUnavailableException(string message) : Exception(message);

/// <summary>
/// Runs solutions with the machine's CPython (<c>python3</c>, or the interpreter named by <c>BRILLIANT_PYTHON</c>), using the very
/// same <c>harness.py</c> and <c>tracer.py</c> the app runs under Pyodide, so a solution that passes here is judged identically in the app.
/// </summary>
public sealed class CPythonRunner : IReferenceSolutionRunner
{
    private const string TestsBootstrap = """
        import json, sys
        sys.path.insert(0, sys.argv[1])
        import harness
        request = json.load(sys.stdin)
        print(harness.run_tests(request["code"], request["entrypoint"], json.dumps(request["tests"])))
        """;

    private const string TraceBootstrap = """
        import json, sys
        sys.path.insert(0, sys.argv[1])
        import tracer
        request = json.load(sys.stdin)
        print(tracer.trace_code(request["code"], request["watch"]))
        """;

    private readonly string _python;
    private readonly TimeSpan _timeout;
    private readonly Lazy<string> _modulesDir = new(ExtractModules);

    public CPythonRunner(TimeSpan? timeout = null, string? python = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _python = python ?? (Environment.GetEnvironmentVariable("BRILLIANT_PYTHON") is { Length: > 0 } p ? p : "python3");
    }

    public TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests)
    {
        var request = System.Text.Json.JsonSerializer.Serialize(new
        {
            code, entrypoint, tests = tests.Select(t => new { input = t.Input, expected = t.Expected, compare = t.Compare }),
        });
        var run = Execute(TestsBootstrap, request);
        if (run.TimedOut)
            return new TestRunResult(TestRunStatus.Error, "",
                $"the solution ran for more than {_timeout.TotalSeconds:0} seconds (infinite loop?).", null, null, []);
        if (run.ExitCode != 0 || run.Stdout.Length == 0)
            return new TestRunResult(TestRunStatus.Error, "", $"Python exited with code {run.ExitCode}.", run.Stderr, null, []);
        return TestRunResultJson.Parse(run.Stdout);
    }

    public TraceResult Trace(string code, IReadOnlyList<Visual> visuals)
    {
        var request = System.Text.Json.JsonSerializer.Serialize(new
        {
            code, watch = TraceResultJson.SerializeWatch(visuals),
        });
        var run = Execute(TraceBootstrap, request);
        if (run.TimedOut)
            return new TraceResult(TraceStatus.Error, [], "",
                $"the code ran for more than {_timeout.TotalSeconds:0} seconds (infinite loop?).", null, null);
        if (run.ExitCode != 0 || run.Stdout.Length == 0)
            return new TraceResult(TraceStatus.Error, [], "", $"Python exited with code {run.ExitCode}.", run.Stderr, null);
        return TraceResultJson.Parse(run.Stdout);
    }

    private sealed record Execution(bool TimedOut, int ExitCode, string Stdout, string Stderr);

    private Execution Execute(string bootstrap, string request)
    {
        var startInfo = new ProcessStartInfo(_python)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(bootstrap);
        startInfo.ArgumentList.Add(_modulesDir.Value);

        Process process;
        try { process = Process.Start(startInfo)!; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new ReferenceRunnerUnavailableException(
                $"could not start '{_python}', which is needed to check reference solutions and trace steps. Install Python 3 or set BRILLIANT_PYTHON.");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(request);
            process.StandardInput.Close();

            if (!process.WaitForExit(_timeout))
            {
                process.Kill(entireProcessTree: true);
                return new Execution(true, -1, "", "");
            }
            return new Execution(false, process.ExitCode, stdout.Result.Trim(), stderr.Result.Trim());
        }
    }

    private static string ExtractModules()
    {
        var dir = Path.Combine(Path.GetTempPath(), "brilliant-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "harness.py", "tracer.py" })
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"{name} is not embedded in the CLI.");
            using var file = File.Create(Path.Combine(dir, name));
            resource.CopyTo(file);
        }
        return dir;
    }
}

internal static class ReferenceSolutionChecker
{
    /// <summary>Proves the write-code solutions, then the trace steps. Stops at the first sign that Python is not available.</summary>
    public static void CheckAll(ValidationReport report, IReferenceSolutionRunner? runner = null)
    {
        var defaults = new Dictionary<string, IReferenceSolutionRunner>(StringComparer.Ordinal);
        IReferenceSolutionRunner Select(string language)
        {
            if (runner is not null) return runner;
            if (!defaults.TryGetValue(language, out var selected)) defaults[language] = selected = DefaultRunner(language);
            return selected;
        }
        if (Check(report.ReferenceChecks, Select, report)) CheckTraces(report.TraceChecks, Select, report);
    }

    /// <summary>Runs every trace step's code once and checks it ran to the end and drew everything the lesson declares.</summary>
    private static void CheckTraces(IReadOnlyList<TraceCheck> checks, Func<string, IReferenceSolutionRunner> select, ValidationReport report)
    {
        foreach (var check in checks)
        {
            var runner = select(check.Language);
            if (!CheckLanguage(check.Language, runner, check.File, check.Label, report)) continue;
            TraceResult result;
            try { result = runner.Trace(check.Code, check.Visuals); }
            catch (ReferenceRunnerUnavailableException ex)
            {
                report.Add(check.File, $"{check.Label}: {ex.Message}");
                return;
            }

            foreach (var problem in DescribeTrace(result, check.Visuals))
                report.Add(check.File, $"{check.Label}: {problem}");
        }
    }

    private static IEnumerable<string> DescribeTrace(TraceResult result, IReadOnlyList<Visual> visuals)
    {
        if (result.Status == TraceStatus.Error)
        {
            var text = new StringBuilder("the code could not be traced");
            if (result.ErrorLine is { } line) text.Append($" (it fails on line {line})");
            text.Append(". ").Append(result.Message ?? "It raised an error.");
            if (!string.IsNullOrEmpty(result.Traceback)) text.Append('\n').Append(Indent(result.Traceback));
            yield return text.ToString();
            yield break;
        }
        if (result.Status == TraceStatus.Truncated)
        {
            yield return $"the code runs for too long to trace. {result.Message}";
            yield break;
        }
        if (!result.Frames.Any(f => f.Line is >= 1))
        {
            yield return "the code has no steps to trace (is it empty, or only comments?).";
            yield break;
        }

        foreach (var visual in visuals)
        {
            if (!result.Frames.Any(f => Draws(f, visual)))
            {
                yield return visual.As switch
                {
                    Visual.Dict => $"variable '{visual.Variable}' is never a dict in the trace, so there is nothing to draw.",
                    Visual.Set => $"variable '{visual.Variable}' is never a set in the trace, so there is nothing to draw.",
                    Visual.Stack or Visual.Queue =>
                        $"variable '{visual.Variable}' is never a list or deque in the trace, so there is no {visual.As} to draw.",
                    Visual.LinkedList =>
                        $"variable '{visual.Variable}' never holds a node (an object with a '{visual.Next}' attribute) in the trace, so there is no list to draw.",
                    Visual.Tree =>
                        $"variable '{visual.Variable}' never holds a tree node (an object with '{visual.Left}' and '{visual.Right}' attributes) in the trace, so there is no tree to draw.",
                    Visual.Heap =>
                        $"variable '{visual.Variable}' is never a list in the trace, so there is no heap to draw.",
                    Visual.Grid =>
                        $"variable '{visual.Variable}' is never a non-empty list of lists or strings in the trace, so there is no grid to draw.",
                    Visual.Graph =>
                        $"variable '{visual.Variable}' is never a non-empty dict in the trace, so there is no graph to draw.",
                    Visual.CallStack => visual.Functions.Count == 0
                        ? "the code never calls a function of its own, so the call stack has nothing to draw."
                        : $"the code never calls {string.Join(" or ", visual.Functions.Select(f => $"'{f}'"))}, so the call stack has nothing to draw.",
                    _ => $"variable '{visual.Variable}' is never a list, tuple or deque in the trace, so there is nothing to draw.",
                };
                continue;
            }
            if (visual.As == Visual.LinkedList)
            {
                foreach (var problem in DescribeList(result, visual)) yield return problem;
                continue;
            }
            if (visual.As == Visual.Tree)
            {
                foreach (var problem in DescribeTree(result, visual)) yield return problem;
                continue;
            }
            if (visual.As == Visual.Grid)
            {
                foreach (var problem in DescribeGrid(result, visual)) yield return problem;
                continue;
            }
            if (visual.As == Visual.Graph)
            {
                foreach (var problem in DescribeGraph(result, visual)) yield return problem;
                continue;
            }
            if (visual.As == Visual.CallStack)
            {
                foreach (var function in visual.Functions)
                    if (!result.Frames.Any(f => f.Calls?.Frames.Any(c => c.Function == function) == true))
                        yield return $"function '{function}' (of the call stack) is never called in the trace.";
                continue;
            }
            foreach (var pointer in visual.Pointers)
            {
                // A sequence's or heap's pointers index cells, so they must be ints; a dict's or set's hold a key, which can be anything.
                var isArray = Visual.Sequences.Contains(visual.As) || visual.As == Visual.Heap;
                if (!result.Frames.Any(f => f.Locals.Any(l => l.Name == pointer && (!isArray || l.IntegerValue() is not null))))
                    yield return isArray
                        ? $"pointer '{pointer}' (of '{visual.Variable}') is never an int variable in the trace."
                        : $"pointer '{pointer}' (of '{visual.Variable}') is never a variable in the trace.";
            }
        }
    }

    /// <summary>A linked list's nodes must have the value attribute, and each pointer must hold one of its nodes at some step.</summary>
    private static IEnumerable<string> DescribeList(TraceResult result, Visual visual)
    {
        var lists = result.Frames.Select(f => f.Lists?.GetValueOrDefault(visual.Variable)).OfType<TrackedList>().ToList();
        if (lists.Any(l => l.Nodes.Any(n => n.Value is null)))
            yield return $"the nodes of '{visual.Variable}' have no attribute '{visual.Value}' to show; " +
                "set 'value' to the attribute that holds a node's value.";
        foreach (var pointer in visual.Pointers)
            if (!lists.Any(l => l.Pointers.TryGetValue(pointer, out var node) && node is not null))
                yield return $"pointer '{pointer}' (of '{visual.Variable}') never holds a node of the list in the trace.";
    }

    /// <summary>
    /// A tree's nodes must have the value attribute, each pointer must hold one of its nodes at some step, and the visited variable must
    /// be a list or set at some step and mark a node at some step.
    /// </summary>
    private static IEnumerable<string> DescribeTree(TraceResult result, Visual visual)
    {
        var trees = result.Frames.Select(f => f.Trees?.GetValueOrDefault(visual.Variable)).OfType<TrackedTree>().ToList();
        if (trees.Any(t => t.Nodes.Any(n => n.Value is null)))
            yield return $"the nodes of '{visual.Variable}' have no attribute '{visual.Value}' to show; " +
                "set 'value' to the attribute that holds a node's value.";
        foreach (var pointer in visual.Pointers)
            if (!trees.Any(t => t.Pointers.TryGetValue(pointer, out var node) && node is not null))
                yield return $"pointer '{pointer}' (of '{visual.Variable}') never holds a node of the tree in the trace.";
        if (visual.Visited is { } visited)
        {
            if (!trees.Any(t => t.Visited is not null))
                yield return $"visited '{visited}' (of '{visual.Variable}') is never a list or set in the trace, so it marks no node.";
            else if (!trees.Any(t => t.Visited is { Nodes.Count: > 0 }))
                yield return $"visited '{visited}' (of '{visual.Variable}') never holds a node of the tree or a node's value in the trace, so it marks no node.";
        }
    }

    /// <summary>
    /// Each grid pointer and pair must land on a cell at some step, each label variable must be a string, list or tuple at some step, and
    /// the highlight must be a list, tuple or set of pairs (or a grid of flags) at some step and mark a cell at some step.
    /// </summary>
    private static IEnumerable<string> DescribeGrid(TraceResult result, Visual visual)
    {
        var grids = result.Frames.Select(f => f.Grids?.GetValueOrDefault(visual.Variable)).OfType<TrackedGrid>().ToList();
        bool OnACell(TrackedGrid g, string name) => g.Pointers.TryGetValue(name, out var at) && at is [var r, var c]
            && r >= 0 && r < g.Rows.Count && c >= 0 && c < g.Rows[r].Count;
        foreach (var pair in visual.Cells)
            if (!grids.Any(g => OnACell(g, pair.Name)))
                yield return $"cells pair [{pair.Row}, {pair.Col}] (of '{visual.Variable}') never indexes a cell of the grid in the trace.";
        foreach (var pointer in visual.Pointers)
            if (!grids.Any(g => OnACell(g, pointer)))
                yield return $"pointer '{pointer}' (of '{visual.Variable}') never holds a (row, col) pair of a cell of the grid in the trace.";
        if (visual.RowLabels is { } rows && !grids.Any(g => g.RowLabels is not null))
            yield return $"rowLabels '{rows}' (of '{visual.Variable}') is never a string, list or tuple in the trace, so it labels no row.";
        if (visual.ColLabels is { } cols && !grids.Any(g => g.ColLabels is not null))
            yield return $"colLabels '{cols}' (of '{visual.Variable}') is never a string, list or tuple in the trace, so it labels no column.";
        if (visual.Highlight is { } highlight)
        {
            if (!grids.Any(g => g.Highlight is not null))
                yield return $"highlight '{highlight}' (of '{visual.Variable}') is never a list, tuple or set of (row, col) pairs, or a grid of True/False, in the trace.";
            else if (!grids.Any(g => g.Highlight is { Cells.Count: > 0 }))
                yield return $"highlight '{highlight}' (of '{visual.Variable}') never marks a cell of the grid in the trace.";
        }
    }

    /// <summary>
    /// Each graph pointer must hold one of its nodes at some step, the edge's two names must hold the ends of one of its edges at some
    /// step, and the visited and frontier variables must be recorded at some step and mark a node at some step.
    /// </summary>
    private static IEnumerable<string> DescribeGraph(TraceResult result, Visual visual)
    {
        var graphs = result.Frames.Select(f => f.Graphs?.GetValueOrDefault(visual.Variable)).OfType<TrackedGraph>().ToList();
        foreach (var pointer in visual.Pointers)
            if (!graphs.Any(g => g.Pointers.TryGetValue(pointer, out var node) && node is not null))
                yield return $"pointer '{pointer}' (of '{visual.Variable}') never holds a node of the graph in the trace.";
        if (visual.Edge is { } edge && !graphs.Any(g => g.Pointers.GetValueOrDefault(edge.From) is { } from
                && g.Pointers.GetValueOrDefault(edge.To) is { } to
                && g.Edges.Any(e => (e.From, e.To) == (from, to) || !visual.Directed && (e.From, e.To) == (to, from))))
            yield return $"edge [{edge.From}, {edge.To}] (of '{visual.Variable}') never holds the two ends of an edge of the graph in the trace.";
        foreach (var (option, name, marks, kinds) in new[]
                 {
                     ("visited", visual.Visited, graphs.Select(g => g.Visited).ToList(), "a list, tuple, deque, set or dict"),
                     ("frontier", visual.Frontier, graphs.Select(g => g.Frontier).ToList(), "a list, tuple or deque"),
                 })
        {
            if (name is null) continue;
            if (!marks.Any(m => m is not null))
                yield return $"{option} '{name}' (of '{visual.Variable}') is never {kinds} in the trace, so it marks no node.";
            else if (!marks.Any(m => m is { Nodes.Count: > 0 }))
                yield return $"{option} '{name}' (of '{visual.Variable}') never holds a node of the graph in the trace, so it marks no node.";
        }
    }

    private static bool Draws(TraceFrame frame, Visual visual) => visual.As switch
    {
        Visual.Graph => frame.Graphs is not null && frame.Graphs.TryGetValue(visual.Variable, out var graph) && graph.Nodes.Count > 0,
        Visual.Grid => frame.Grids is not null && frame.Grids.TryGetValue(visual.Variable, out var grid) && grid.Rows.Count > 0,
        Visual.Heap => frame.Heaps is not null && frame.Heaps.ContainsKey(visual.Variable),
        Visual.Tree => frame.Trees is not null && frame.Trees.TryGetValue(visual.Variable, out var tree) && tree.Root is not null,
        Visual.LinkedList => frame.Lists is not null && frame.Lists.TryGetValue(visual.Variable, out var list) && list.Head is not null,
        Visual.CallStack => frame.Calls is { Frames.Count: > 0 },
        _ when Visual.Sequences.Contains(visual.As) => frame.Tracked.TryGetValue(visual.Variable, out var tracked) && tracked.Draws(visual.As),
        _ => frame.Tables is not null && frame.Tables.TryGetValue(visual.Variable, out var table) && table.Kind == visual.As,
    };

    /// <summary>Runs every reference solution against its step's tests and reports each failure with lesson, step, test, expected and actual. False when Python was not available.</summary>
    private static bool Check(IReadOnlyList<ReferenceCheck> checks, Func<string, IReferenceSolutionRunner> select, ValidationReport report)
    {
        foreach (var check in checks)
        {
            var runner = select(check.Language);
            if (!CheckLanguage(check.Language, runner, check.File, check.Label, report)) continue;
            TestRunResult result;
            try { result = check.Files is { } files ? runner.Run(files, check.Entrypoint, check.Tests) : runner.Run(check.Solution, check.Entrypoint, check.Tests); }
            catch (ReferenceRunnerUnavailableException ex)
            {
                report.Add(check.File, $"{check.Label}: {ex.Message}");
                return false; // one report is enough; every other solution would fail the same way
            }

            var problem = Describe(result, check.Tests.Count);
            if (problem is not null) report.Add(check.File, $"{check.Label}: reference solution is wrong. {problem}");
        }
        return true;
    }

    private static IReferenceSolutionRunner DefaultRunner(string language) => language == "csharp"
        ? new CSharpReferenceSolutionRunner() : new CPythonRunner();

    private static bool CheckLanguage(string language, IReferenceSolutionRunner runner, string file, string label, ValidationReport report)
    {
        if (runner.Language == language) return true;
        report.Add(file, $"{label}: no reference/trace checker is available for '{language}' (selected checker: '{runner.Language}'). Publishing requires a checker for the exercise language.");
        return false;
    }

    private static string? Describe(TestRunResult result, int testCount)
    {
        switch (result.Status)
        {
            case TestRunStatus.Passed:
                return null;
            case TestRunStatus.TimedOut:
            case TestRunStatus.Error:
                var text = new StringBuilder(result.Message ?? "It could not be run.");
                if (!string.IsNullOrEmpty(result.Traceback)) text.Append('\n').Append(Indent(result.Traceback));
                return text.ToString();
        }

        var failed = result.Tests.Select((t, i) => (Test: t, Number: i + 1)).Where(x => !x.Test.Passed).ToList();
        var lines = new StringBuilder($"It fails {failed.Count} of {testCount} tests:");
        foreach (var (test, number) in failed)
        {
            lines.Append($"\n  test {number}: {test.Call}");
            if (test.Allocation is { Exceeded: true, Enforced: true } allocation)
            {
                lines.Append($"\n    allocated {allocation.Bytes:N0} bytes per call; the budget is {allocation.Budget:N0} ({allocation.Runtime})");
                continue;
            }
            lines.Append($"\n    expected: {test.Expected}");
            lines.Append(test.Actual is null ? "\n    actual:   (raised an error)" : $"\n    actual:   {test.Actual}");
            if (!string.IsNullOrEmpty(test.Traceback)) lines.Append('\n').Append(Indent(test.Traceback, "    "));
        }
        return lines.ToString();
    }

    private static string Indent(string text, string by = "  ") => string.Join('\n', text.Split('\n').Select(l => by + l));
}
