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
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(CSharpReferenceSolutionRunner).Assembly.Location);
        start.ArgumentList.Add("--csharp-reference-worker");
        try
        {
            using var process = Process.Start(start) ?? throw new ReferenceRunnerUnavailableException("could not start the C# reference worker.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(JsonSerializer.Serialize(new Request(files, entrypoint, tests), Json));
            process.StandardInput.Close();
            if (!process.WaitForExit(_timeout))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return Error($"C# reference worker exceeded {_timeout.TotalSeconds:0.##} seconds.", TestRunStatus.TimedOut);
            }
            var output = stdout.GetAwaiter().GetResult();
            var errors = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0) return Error($"C# reference worker exited with code {process.ExitCode}. {errors}");
            try { return JsonSerializer.Deserialize<TestRunResult>(output, Json) ?? Error("C# reference worker returned no result."); }
            catch (JsonException) { return Error("C# reference worker returned an invalid result."); }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ReferenceRunnerUnavailableException($"could not start the .NET 10 C# reference worker: {ex.Message}");
        }
    }

    public TraceResult Trace(string code, IReadOnlyList<Visual> visuals) =>
        new(TraceStatus.Error, [], "", "C# recorded trace publishing follows in #153; a Python checker cannot validate C#.", null, null);

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
