using System.Diagnostics;
using System.Runtime.CompilerServices;
using Brilliant.Code.CSharp;
using Brilliant.Core.Code;

namespace Brilliant.Runtime.CSharp;

/// <summary>
/// Bytes allocated per call, measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/> on the calling thread. Warm-up calls run first so
/// one-off costs (static initialisation, a cached lambda) are excluded; the result is the smallest of several samples, so a stray
/// runtime allocation inside one sample doesn't fail a method that allocates nothing. Allocation sizes differ between CoreCLR and Mono
/// (#138), so only CoreCLR numbers are authoritative.
/// </summary>
public static class AllocationMeter
{
    public const int WarmUpCalls = 50;
    public const int Samples = 5;
    public const int CallsPerSample = 50;

    /// <summary>Names the runtime in feedback so a device number is never presented as a CoreCLR number.</summary>
    public static string RuntimeName { get; } = Type.GetType("Mono.RuntimeStructs") is not null
        ? (RuntimeFeature.IsDynamicCodeCompiled ? "Mono" : "Mono interpreter")
        : "CoreCLR";

    /// <summary>Whether this process may enforce a budget: pack-time budgets are defined by CoreCLR.</summary>
    public static bool IsAuthoritative => RuntimeName == "CoreCLR";

    /// <summary>
    /// Measures <c>run(test, 1)</c>, which the generated harness runs as one call of the entrypoint. <paramref name="firstCall"/> is how long
    /// the already-checked call took. Returns null, having started no call that could end after <paramref name="limit"/>, when a complete
    /// sample can't fit: a slow call must never turn a passing run into a timeout.
    /// </summary>
    public static long? Measure(Func<int, int, object?[]> run, int test, ProbeBudget budget, TimeSpan firstCall, TimeSpan? limit = null)
    {
        var started = Stopwatch.GetTimestamp();
        var slowest = firstCall;
        bool Fits() => limit is not { } l || Stopwatch.GetElapsedTime(started) + slowest <= l;
        void Call()
        {
            budget.ResetCounts();
            var begun = Stopwatch.GetTimestamp();
            run(test, 1);
            var took = Stopwatch.GetElapsedTime(begun);
            if (took > slowest) slowest = took;
        }
        for (var i = 0; i < WarmUpCalls; i++)
        {
            if (!Fits()) return null;
            Call();
        }
        // Collect now so a pending collection doesn't land inside a sample.
        GC.Collect();
        long? best = null;
        for (var sample = 0; sample < Samples; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < CallsPerSample; i++)
            {
                // Abandon an unfinished sample, but keep any complete one.
                if (!Fits()) return best;
                Call();
            }
            var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / CallsPerSample;
            if (best is null || perCall < best) best = perCall;
        }
        return best;
    }
}
