using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Brilliant.Code.CSharp;
using Brilliant.Core.Code;
using Brilliant.Core.Content;
using Brilliant.Runtime.CSharp;

namespace Brilliant.Cli;

/// <summary>CoreCLR publishing gate. A killable child process contains blocking library calls and process-wide effects.</summary>
public sealed class CSharpReferenceSolutionRunner(TimeSpan? timeout = null) : IReferenceSolutionRunner
{
    public string Language => "csharp";
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests) =>
        Run([new CodeFile(CodeFiles.SingleFileName, code)], entrypoint, tests);

    public TestRunResult Run(IReadOnlyList<CodeFile> files, string entrypoint, IReadOnlyList<CodeTest> tests)
    {
        var worked = RunInWorker("--csharp-reference-worker", "reference", JsonSerializer.Serialize(new Request(files, entrypoint, tests), Json));
        if (worked.Failure is { } failure) return Error(failure, worked.TimedOut ? TestRunStatus.TimedOut : TestRunStatus.Error);
        try { return JsonSerializer.Deserialize<TestRunResult>(worked.Output!, Json) ?? Error("C# reference worker returned no result."); }
        catch (JsonException) { return Error("C# reference worker returned an invalid result."); }
    }

    /// <summary>
    /// Records the example in a killable child process, as the reference solutions are run, and returns what it recorded: the same
    /// frames the Python tracer gives, which the pack carries so no device ever runs the C#.
    /// </summary>
    public TraceResult Trace(string code, IReadOnlyList<Visual> visuals)
    {
        var request = new TraceRequest(code, visuals.Select(v => new WatchedVariable(v.Variable, v.As)).ToArray());
        var worked = RunInWorker("--csharp-trace-worker", "trace", JsonSerializer.Serialize(request, Json));
        if (worked.Failure is { } failure) return new(worked.TimedOut ? TraceStatus.Truncated : TraceStatus.Error, [], "", failure, null, null);
        try { return TraceResultJson.Parse(worked.Output!); }
        catch (JsonException) { return new(TraceStatus.Error, [], "", "C# trace worker returned an invalid result.", null, null); }
    }

    private sealed record Worked(string? Output, string? Failure, bool TimedOut = false);

    private Worked RunInWorker(string flag, string name, string input)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(CSharpReferenceSolutionRunner).Assembly.Location);
        start.ArgumentList.Add(flag);
        try
        {
            using var process = Process.Start(start) ?? throw new ReferenceRunnerUnavailableException($"could not start the C# {name} worker.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(input);
            process.StandardInput.Close();
            if (!process.WaitForExit(_timeout))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return new(null, $"C# {name} worker exceeded {_timeout.TotalSeconds:0.##} seconds.", TimedOut: true);
            }
            var output = stdout.GetAwaiter().GetResult();
            var errors = stderr.GetAwaiter().GetResult();
            return process.ExitCode != 0 ? new(null, $"C# {name} worker exited with code {process.ExitCode}. {errors}") : new(output, null);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ReferenceRunnerUnavailableException($"could not start the .NET 10 C# {name} worker: {ex.Message}");
        }
    }

    private sealed record TraceRequest(string Code, IReadOnlyList<WatchedVariable> Watched);
    private sealed record WatchedVariable(string Variable, string As);

    public static int RunTraceWorker(TextReader input, TextWriter output)
    {
        TraceResult result;
        try
        {
            var request = JsonSerializer.Deserialize<TraceRequest>(input.ReadToEnd(), Json)
                ?? throw new InvalidOperationException("Missing C# trace request.");
            result = CSharpTraceRecorder.Record(request.Code, request.Watched.Select(w => new Visual(w.Variable, w.As, [])).ToArray());
        }
        catch (Exception ex) { result = new(TraceStatus.Error, [], "", ex.Message, null, null); }
        output.Write(TraceResultJson.Serialize(result));
        return 0;
    }

    public static int RunWorker(TextReader input, TextWriter output)
    {
        TestRunResult result;
        try
        {
            var request = JsonSerializer.Deserialize<Request>(input.ReadToEnd(), Json)
                ?? throw new InvalidOperationException("Missing C# reference request.");
            result = CompileAndRun(request);
        }
        catch (Exception ex) { result = Error(ex.Message); }
        output.Write(JsonSerializer.Serialize(result, Json));
        return 0;
    }

    private static readonly TimeSpan MeasureLimit = TimeSpan.FromSeconds(5);

    private sealed record Request(IReadOnlyList<CodeFile> Files, string Entrypoint, IReadOnlyList<CodeTest> Tests);

    private static TestRunResult CompileAndRun(Request request)
    {
        var compiled = CSharpSubmissionCompiler.Compile(request.Files, request.Entrypoint, request.Tests);
        if (compiled.Error is { } error) return error;
        using var pe = new MemoryStream(compiled.Assembly!);
        pe.Position = 0;
        var context = new AssemblyLoadContext("C# reference", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(pe);
            var method = assembly.GetType("__BrilliantReference")!.GetMethod("Run")!.CreateDelegate<Func<int, int, object?[]>>();
            var outcomes = new List<TestOutcome>();
            for (var i = 0; i < request.Tests.Count; i++)
            {
                var index = i;
                object?[]? pair = null;
                Exception? failure = null;
                string? actual = null;
                string? expected = null;
                AllocationReport? allocation = null;
                string? measureFailure = null;
                var passed = false;
                using var printed = new StringWriter();
                var previous = Console.Out;
                var previousError = Console.Error;
                var thread = new Thread(() =>
                {
                    var budget = new ProbeBudget();
                    ProbeBudget.Current = budget;
                    try
                    {
                        var started = Stopwatch.GetTimestamp();
                        pair = method(index, 0);
                        var firstCall = Stopwatch.GetElapsedTime(started);
                        budget.Tick();
                        passed = CSharpValues.Equal(pair[0], pair[1]);
                        actual = CSharpValues.Format(pair[0]);
                        expected = CSharpValues.Format(pair[1]);
                        if (passed && request.Tests[index].MaxAllocatedBytes is { } allowed)
                        {
                            // Printing while measuring is discarded; the checked run above already captured the output.
                            Console.SetOut(TextWriter.Null);
                            if (AllocationMeter.Measure(method, index, budget, firstCall, MeasureLimit) is { } bytes)
                            {
                                allocation = new(bytes, allowed, AllocationMeter.RuntimeName, Enforced: AllocationMeter.IsAuthoritative);
                                passed = !(allocation.Enforced && allocation.Exceeded);
                            }
                            else measureFailure = $"The allocation of {request.Entrypoint}({request.Tests[index].Input}) couldn't be measured within {MeasureLimit.TotalSeconds:0.##} seconds; it is too slow to repeat.";
                        }
                    }
                    catch (Exception ex) { failure = Unwrap(ex); passed = false; }
                    finally { ProbeBudget.Current = null; }
                }, ProbeBudget.DefaultStackSize) { IsBackground = true, Name = "C# reference test" };
                Console.SetOut(printed);
                Console.SetError(printed);
                try { thread.Start(); thread.Join(); }
                finally { Console.SetOut(previous); Console.SetError(previousError); }
                if (failure is BudgetExceededException) return Error(failure.Message, TestRunStatus.TimedOut);
                if (measureFailure is not null) return Error(measureFailure);
                var test = request.Tests[i];
                outcomes.Add(new TestOutcome($"{request.Entrypoint}({test.Input})", expected ?? test.Expected,
                    actual, passed,
                    printed.ToString(), failure?.ToString(), null, allocation));
            }
            return new TestRunResult(outcomes.All(t => t.Passed) ? TestRunStatus.Passed : TestRunStatus.Failed, "", null, null, null, outcomes);
        }
        finally { context.Unload(); }
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : ex;
    private static TestRunResult Error(string message, TestRunStatus status = TestRunStatus.Error) => new(status, "", message, null, null, []);
}
