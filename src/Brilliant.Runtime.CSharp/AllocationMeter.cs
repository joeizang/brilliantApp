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
    /// Measures <c>run(test, 1)</c>, which the generated harness runs as one call of the entrypoint. Returns null when
    /// <paramref name="limit"/> elapses before a complete sample.
    /// </summary>
    public static long? Measure(Func<int, int, object?[]> run, int test, ProbeBudget budget, TimeSpan? limit = null)
    {
        var started = Stopwatch.GetTimestamp();
        bool Expired() => limit is { } l && Stopwatch.GetElapsedTime(started) > l;
        for (var i = 0; i < WarmUpCalls; i++)
        {
            budget.ResetCounts();
            run(test, 1);
            if (i > 0 && Expired()) return null;
        }
        // Collect now so a pending collection doesn't land inside a sample.
        GC.Collect();
        long? best = null;
        for (var sample = 0; sample < Samples; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var complete = true;
            for (var i = 0; i < CallsPerSample; i++)
            {
                budget.ResetCounts();
                run(test, 1);
                if (Expired()) { complete = false; break; }
            }
            var used = GC.GetAllocatedBytesForCurrentThread() - before;
            if (!complete) break;
            var perCall = used / CallsPerSample;
            if (best is null || perCall < best) best = perCall;
        }
        return best;
    }
}
