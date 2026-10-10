using System.Diagnostics;
using System.Reflection;
using System.Text;
using Brilliant.Core.Content;
using Brilliant.Core.Python;

namespace Brilliant.Cli;

/// <summary>A write-code exercise's reference solution, waiting to be proved against the step's own hidden tests.</summary>
internal sealed record ReferenceCheck(string File, string Label, string StepId, string Entrypoint, string Solution, IReadOnlyList<CodeTest> Tests);

/// <summary>Runs Python source against an exercise's tests. The real implementation uses CPython; tests substitute a fake.</summary>
public interface IReferenceSolutionRunner
{
    /// <exception cref="ReferenceRunnerUnavailableException">Python couldn't be started at all.</exception>
    TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests);
}

public sealed class ReferenceRunnerUnavailableException(string message) : Exception(message);

/// <summary>
/// Runs solutions with the machine's CPython (<c>python3</c>, or the interpreter named by <c>BRILLIANT_PYTHON</c>), using the very
/// same <c>harness.py</c> the app runs under Pyodide, so a solution that passes here is judged identically in the app.
/// </summary>
public sealed class CPythonRunner : IReferenceSolutionRunner
{
    private const string Bootstrap = """
        import json, sys
        sys.path.insert(0, sys.argv[1])
        import harness
        request = json.load(sys.stdin)
        print(harness.run_tests(request["code"], request["entrypoint"], json.dumps(request["tests"])))
        """;

    private readonly string _python;
    private readonly TimeSpan _timeout;
    private readonly Lazy<string> _harnessDir = new(ExtractHarness);

    public CPythonRunner(TimeSpan? timeout = null, string? python = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _python = python ?? (Environment.GetEnvironmentVariable("BRILLIANT_PYTHON") is { Length: > 0 } p ? p : "python3");
    }

    public TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests)
    {
        var startInfo = new ProcessStartInfo(_python)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(Bootstrap);
        startInfo.ArgumentList.Add(_harnessDir.Value);

        Process process;
        try { process = Process.Start(startInfo)!; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new ReferenceRunnerUnavailableException(
                $"could not start '{_python}', which is needed to check write-code reference solutions. Install Python 3 or set BRILLIANT_PYTHON.");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var request = System.Text.Json.JsonSerializer.Serialize(new
            {
                code, entrypoint, tests = tests.Select(t => new { input = t.Input, expected = t.Expected }),
            });
            process.StandardInput.Write(request);
            process.StandardInput.Close();

            if (!process.WaitForExit(_timeout))
            {
                process.Kill(entireProcessTree: true);
                return new TestRunResult(TestRunStatus.Error, "",
                    $"the solution ran for more than {_timeout.TotalSeconds:0} seconds (infinite loop?).", null, null, []);
            }

            var output = stdout.Result.Trim();
            if (process.ExitCode != 0 || output.Length == 0)
                return new TestRunResult(TestRunStatus.Error, "", $"Python exited with code {process.ExitCode}.", stderr.Result.Trim(), null, []);
            return TestRunResultJson.Parse(output);
        }
    }

    private static string ExtractHarness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "brilliant-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("harness.py")
            ?? throw new InvalidOperationException("harness.py is not embedded in the CLI.");
        using var file = File.Create(Path.Combine(dir, "harness.py"));
        resource.CopyTo(file);
        return dir;
    }
}

internal static class ReferenceSolutionChecker
{
    /// <summary>Runs every reference solution against its step's tests and reports each failure with lesson, step, test, expected and actual.</summary>
    public static void Check(IReadOnlyList<ReferenceCheck> checks, IReferenceSolutionRunner runner, ValidationReport report)
    {
        foreach (var check in checks)
        {
            TestRunResult result;
            try { result = runner.Run(check.Solution, check.Entrypoint, check.Tests); }
            catch (ReferenceRunnerUnavailableException ex)
            {
                report.Add(check.File, $"{check.Label}: {ex.Message}");
                return; // one report is enough; every other solution would fail the same way
            }

            var problem = Describe(result, check.Tests.Count);
            if (problem is not null) report.Add(check.File, $"{check.Label}: reference solution is wrong. {problem}");
        }
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
