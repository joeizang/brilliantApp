using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Brilliant.Code.CSharp;
using Brilliant.Core.Code;
using Brilliant.Core.Content;

namespace Brilliant.Runtime.CSharp;

public sealed record CSharpSessionDiagnostics(int AssemblyLoads, int LoadLimit, int AliveContexts, long WorkingSetBytes, bool RequiresRestart, int CachedSubmissions);

/// <summary>Synchronous C# exercises. Native hosts share one instance per process. Cooperative probes cannot kill a blocked thread.</summary>
public sealed class CSharpRuntime(TimeSpan? testTimeout = null) : ICodeRuntime
{
    public string Language => "csharp";
    public const int AssemblyLoadLimit = 50;
    /// <summary>Test seam: lets a submission block so the watchdog can be exercised. Learner code never runs with this set.</summary>
    internal bool AllowBlockingCalls { get; init; }
    /// <summary>
    /// Whether an exceeded allocation budget fails a test. Only CoreCLR measurements are authoritative (#138); on Mono the measurement is
    /// advisory and named as such. The default follows the running runtime.
    /// </summary>
    public bool EnforceAllocationBudgets { get; init; } = AllocationMeter.IsAuthoritative;
    public string AllocationRuntime { get; init; } = AllocationMeter.RuntimeName;
    private readonly TimeSpan _testTimeout = testTimeout is { } t && t <= TimeSpan.Zero ? throw new ArgumentOutOfRangeException(nameof(testTimeout)) : testTimeout ?? TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _runs = new(1);
    private readonly object _state = new();
    private readonly List<WeakReference> _contexts = [];
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private int _loads;
    private bool _blocked;

    public CSharpSessionDiagnostics Diagnostics
    {
        get
        {
            lock (_state) return new(_loads, AssemblyLoadLimit, _contexts.Count(c => c.IsAlive),
                Environment.WorkingSet, _blocked || _loads >= AssemblyLoadLimit, _cache.Count);
        }
    }

    public async Task<TestRunResult> RunTestsAsync(string code, string entrypoint, IReadOnlyList<CodeTest> tests, CancellationToken cancellationToken = default)
    {
        await _runs.WaitAsync(cancellationToken);
        try
        {
            if (Diagnostics.RequiresRestart) return Error("This C# session needs a restart before another run. Your draft is saved; quit and reopen the app.");
            // Copy caller-owned lists before asynchronous work and bound input before constructing a cache key.
            var copy = tests.ToArray();
            if (code.Length > 65_536 || copy.Length is < 1 or > 64 || copy.Any(t => t.Input.Length > 4_096 || t.Expected.Length > 4_096))
                return Error("C# exercises support up to 64 tests, 64 KiB of source and 4 KiB per test expression.");
            var key = JsonSerializer.Serialize(new { code, entrypoint, tests = copy });
            byte[]? image;
            lock (_state) _cache.TryGetValue(key, out image);
            if (image is null)
            {
                using var compilationStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                compilationStop.CancelAfter(TimeSpan.FromSeconds(15));
                var compilation = Task.Run(() => CSharpSubmissionCompiler.Compile(code, entrypoint, copy, captureConsole: true, AllowBlockingCalls, compilationStop.Token), compilationStop.Token);
                CSharpSubmissionCompiler.Compilation compiled;
                try { compiled = await compilation.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    compilationStop.Cancel();
                    // Roslyn usually honours cancellation quickly; only a compile that ignores it poisons the session.
                    try { await compilation.WaitAsync(TimeSpan.FromMilliseconds(100)); }
                    catch (Exception unwound) when (unwound is TimeoutException) { lock (_state) _blocked = true; }
                    catch (Exception unwound) when (unwound is OperationCanceledException) { }
                    // Observe late faults without retaining its emitted result.
                    _ = compilation.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    return Error("C# compilation was stopped. Quit and reopen the app if another run requires a restart; your draft is saved.", TestRunStatus.TimedOut);
                }
                if (compiled.Error is { } error) return error;
                image = compiled.Assembly!;
                lock (_state)
                {
                    if (_cache.Count == 8) _cache.Remove(_cacheOrder.Dequeue());
                    _cache.Add(key, image); _cacheOrder.Enqueue(key);
                }
            }
            return await RunCompiled(image, entrypoint, copy, cancellationToken);
        }
        catch (OperationCanceledException) { return Error("The C# run was cancelled.", TestRunStatus.TimedOut); }
        catch (Exception ex) { return Error("Couldn't run C#: " + ex.Message); }
        finally { _runs.Release(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<TestRunResult> RunCompiled(byte[] image, string entrypoint, IReadOnlyList<CodeTest> tests, CancellationToken cancellationToken)
    {
        var context = new AssemblyLoadContext("C# submission", isCollectible: true);
        lock (_state) { _loads++; _contexts.Add(new(context)); }
        Task<TestExecution>? pending = null;
        try
        {
            using var pe = new MemoryStream(image);
            var method = context.LoadFromStream(pe).GetType("__BrilliantReference")!.GetMethod("Run")!.CreateDelegate<Func<int, int, object?[]>>();
            var outcomes = new List<TestOutcome>();
            for (var i = 0; i < tests.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var budget = new ProbeBudget(timeout: _testTimeout);
                pending = Execute(method, i, tests[i], entrypoint, budget);
                TestExecution execution;
                try { execution = await pending.WaitAsync(_testTimeout, cancellationToken); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    budget.Cancel();
                    // Give a running probe a chance to unwind; never wait indefinitely for library code.
                    try { await pending.WaitAsync(TimeSpan.FromMilliseconds(100)); }
                    catch (TimeoutException) { lock (_state) _blocked = true; }
                    var restart = Diagnostics.RequiresRestart ? " Quit and reopen the app before running again; your draft is saved." : "";
                    return Error($"C# test {i + 1} ({entrypoint}({tests[i].Input})) exceeded its time budget.{restart}", TestRunStatus.TimedOut);
                }
                if (execution.Timeout is { } timeout)
                    return Error($"C# test {i + 1} ({entrypoint}({tests[i].Input})): {timeout}",
                        timeout == SubmissionOutput.LimitMessage ? TestRunStatus.Error : TestRunStatus.TimedOut);
                outcomes.Add(execution.Outcome!);
            }
            return new(outcomes.All(t => t.Passed) ? TestRunStatus.Passed : TestRunStatus.Failed, "", null, null, null, outcomes);
        }
        finally
        {
            if (pending is { IsCompleted: false })
                _ = pending.ContinueWith(_ => context.Unload(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else context.Unload();
        }
    }

    private sealed record TestExecution(TestOutcome? Outcome, string? Timeout = null);

    private Task<TestExecution> Execute(Func<int, int, object?[]> method, int index, CodeTest test, string entrypoint, ProbeBudget budget)
    {
        var done = new TaskCompletionSource<TestExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var printed = new SubmissionOutput();
            ProbeBudget.Current = budget;
            ProbeConsole.Writer = printed;
            try
            {
                var started = Stopwatch.GetTimestamp();
                var pair = method(index, 0);
                budget.Tick();
                var passed = CSharpValues.Equal(pair[0], pair[1]);
                var actual = CSharpValues.Format(pair[0]);
                var expected = CSharpValues.Format(pair[1]);
                budget.Tick();
                var output = printed.ToString();
                AllocationReport? allocation = null;
                if (passed && test.MaxAllocatedBytes is { } allowed)
                {
                    // Printing during the measurement is discarded; one run's output is enough.
                    ProbeConsole.Writer = TextWriter.Null;
                    var remaining = _testTimeout - Stopwatch.GetElapsedTime(started);
                    if (AllocationMeter.Measure(method, index, budget, remaining / 2) is { } bytes)
                    {
                        allocation = new(bytes, allowed, AllocationRuntime, EnforceAllocationBudgets);
                        passed = !(allocation.Enforced && allocation.Exceeded);
                    }
                }
                done.SetResult(new(new($"{entrypoint}({test.Input})", expected, actual, passed, output, null, null, allocation)));
            }
            catch (Exception ex)
            {
                var failure = Unwrap(ex);
                done.SetResult(failure is BudgetExceededException ? new(null, failure.Message) :
                    new(new($"{entrypoint}({test.Input})", test.Expected, null, false, printed.ToString(), Describe(failure), null)));
            }
            finally { ProbeConsole.Writer = null; ProbeBudget.Current = null; }
        }, ProbeBudget.DefaultStackSize) { IsBackground = true, Name = "C# exercise" };
        thread.Start();
        return done.Task;
    }

    private static string Describe(Exception failure)
    {
        // A learner exception can override Message/ToString. Never call either virtual member on it.
        var type = failure.GetType();
        var message = type.Assembly == typeof(Exception).Assembly ? failure.Message : "The code threw an exception.";
        return type.Name + ": " + (message.Length > 2_048 ? message[..2_048] : message);
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : ex;
    private static TestRunResult Error(string message, TestRunStatus status = TestRunStatus.Error) => new(status, "", message, null, null, []);
    public Task<TraceResult> TraceAsync(string code, IReadOnlyList<Visual> visuals, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TraceResult(TraceStatus.Error, [], "", "C# traces use recorded content; live tracing is not available.", null, null));
}
