using Brilliant.Core.Content;

namespace Brilliant.Core.Python;

/// <summary>
/// Whether the tests could run at all. <see cref="Error"/> means the learner's code never got as far as the
/// tests (syntax error, an exception while loading, or the required function is missing).
/// <see cref="TimedOut"/> means the run was stopped for taking too long (e.g. an infinite loop); no test results exist.
/// </summary>
public enum TestRunStatus { Passed, Failed, Error, TimedOut }

/// <summary>
/// The outcome of one hidden test. <see cref="Call"/> is the call that was made (e.g. <c>min_max([3, 1, 2])</c>);
/// <see cref="Expected"/> and <see cref="Actual"/> are Python reprs. <see cref="Actual"/> is null when the call raised.
/// </summary>
public sealed record TestOutcome(
    string Call,
    string Expected,
    string? Actual,
    bool Passed,
    string Stdout,
    string? Traceback,
    int? ErrorLine);

/// <summary>A labelled chunk of text the learner's code printed.</summary>
public sealed record OutputSection(string Label, string Text);

/// <summary>The result of running a submission against all of a step's tests.</summary>
public sealed record TestRunResult(
    TestRunStatus Status,
    string Stdout,
    string? Message,
    string? Traceback,
    int? ErrorLine,
    IReadOnlyList<TestOutcome> Tests)
{
    public int PassedCount => Tests.Count(t => t.Passed);
    public TestOutcome? FirstFailure => Tests.FirstOrDefault(t => !t.Passed);

    /// <summary>
    /// Everything the code printed, whatever the verdict: first while the file loaded, then during each test call
    /// (labelled with the call). Sections with no output are left out.
    /// </summary>
    public IReadOnlyList<OutputSection> Outputs
    {
        get
        {
            var sections = new List<OutputSection>();
            if (!string.IsNullOrEmpty(Stdout))
                sections.Add(new(Status == TestRunStatus.Error ? "Output before the error" : "Output when your file loaded", Stdout));
            foreach (var test in Tests)
                if (!string.IsNullOrEmpty(test.Stdout))
                    sections.Add(new($"Printed during {test.Call}", test.Stdout));
            return sections;
        }
    }
}

/// <summary>Runs learner Python. Implemented over Pyodide in a Web Worker by the app host (works offline).</summary>
public interface IPythonRuntime
{
    /// <summary>
    /// Loads <paramref name="code"/> and calls <paramref name="entrypoint"/> once per test, capturing stdout and tracebacks.
    /// Never throws for problems in the learner's code; those come back in the result.
    /// </summary>
    Task<TestRunResult> RunTestsAsync(string code, string entrypoint, IReadOnlyList<CodeTest> tests, CancellationToken cancellationToken = default);
}

/// <summary>Reads the JSON the Python test harness (wwwroot/python/harness.py) returns.</summary>
public static class TestRunResultJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
    };

    public static TestRunResult Parse(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<TestRunResult>(json, Options)
        ?? throw new InvalidOperationException("The Python runtime returned an empty result.");

    /// <summary>The tests as the harness expects them: <c>[{"input": ..., "expected": ...}]</c>.</summary>
    public static string SerializeTests(IEnumerable<CodeTest> tests) =>
        System.Text.Json.JsonSerializer.Serialize(tests.Select(t => new { input = t.Input, expected = t.Expected }));
}
