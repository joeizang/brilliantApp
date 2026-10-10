using System.Diagnostics;
using System.Reflection;
using System.Text;
using Brilliant.Core.Content;
using Brilliant.Core.Python;

namespace Brilliant.Cli;

/// <summary>A write-code exercise's reference solution, waiting to be proved against the step's own hidden tests.</summary>
internal sealed record ReferenceCheck(string File, string Label, string StepId, string Entrypoint, string Solution, IReadOnlyList<CodeTest> Tests);

/// <summary>A trace step's code, waiting to be run to prove it works and draws the variables the lesson says it does.</summary>
internal sealed record TraceCheck(string File, string Label, string Code, IReadOnlyList<Visual> Visuals);

/// <summary>Runs Python source against an exercise's tests. The real implementation uses CPython; tests substitute a fake.</summary>
public interface IReferenceSolutionRunner
{
    /// <exception cref="ReferenceRunnerUnavailableException">Python couldn't be started at all.</exception>
    TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests);

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
        print(tracer.trace_code(request["code"], json.dumps(request["watch"])))
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
            code, entrypoint, tests = tests.Select(t => new { input = t.Input, expected = t.Expected }),
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
            code, watch = visuals.Where(v => v.As == Visual.Array).Select(v => v.Variable),
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
    public static void CheckAll(ValidationReport report, IReferenceSolutionRunner runner)
    {
        if (Check(report.ReferenceChecks, runner, report)) CheckTraces(report.TraceChecks, runner, report);
    }

    /// <summary>Runs every trace step's code once and checks it ran to the end and drew everything the lesson declares.</summary>
    private static void CheckTraces(IReadOnlyList<TraceCheck> checks, IReferenceSolutionRunner runner, ValidationReport report)
    {
        foreach (var check in checks)
        {
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
        if (result.Frames.Count == 0)
        {
            yield return "the code has no steps to trace (is it empty?).";
            yield break;
        }

        foreach (var visual in visuals)
        {
            if (!result.Frames.Any(f => f.Tracked.ContainsKey(visual.Variable)))
            {
                yield return $"variable '{visual.Variable}' is never a list or tuple in the trace, so there is nothing to draw.";
                continue;
            }
            foreach (var pointer in visual.Pointers)
                if (!result.Frames.Any(f => f.Locals.Any(l => l.Name == pointer && l.Type == "int")))
                    yield return $"pointer '{pointer}' (of '{visual.Variable}') is never an int variable in the trace.";
        }
    }

    /// <summary>Runs every reference solution against its step's tests and reports each failure with lesson, step, test, expected and actual. False when Python was not available.</summary>
    private static bool Check(IReadOnlyList<ReferenceCheck> checks, IReferenceSolutionRunner runner, ValidationReport report)
    {
        foreach (var check in checks)
        {
            TestRunResult result;
            try { result = runner.Run(check.Solution, check.Entrypoint, check.Tests); }
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

    private static string? Describe(TestRunResult result, int testCount)
    {
        switch (result.Status)
        {
            case TestRunStatus.Passed:
                return null;
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
            lines.Append($"\n    expected: {test.Expected}");
            lines.Append(test.Actual is null ? "\n    actual:   (raised an error)" : $"\n    actual:   {test.Actual}");
            if (!string.IsNullOrEmpty(test.Traceback)) lines.Append('\n').Append(Indent(test.Traceback, "    "));
        }
        return lines.ToString();
    }

    private static string Indent(string text, string by = "  ") => string.Join('\n', text.Split('\n').Select(l => by + l));
}
