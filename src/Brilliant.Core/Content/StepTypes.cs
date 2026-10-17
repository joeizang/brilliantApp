using System.Collections.Frozen;

namespace Brilliant.Core.Content;

/// <summary>
/// The closed set of step keys an author may write and an app may enable. Each is a JSON discriminator of <see cref="Step"/> except
/// <c>allocations</c>, which is authored as one step and packed as a predict-output step (variant predict) or a write-code step (variant rewrite).
/// </summary>
public static class StepTypes
{
    public static IReadOnlySet<string> All { get; } = new[]
    {
        "explain", "choice", "predict-output", "write-code", "fill-blank", "parsons", "trace", "async-timeline", "lowered", "benchmark-predict", "allocations", "interleave", "review-code",
    }.ToFrozenSet(StringComparer.Ordinal);
}
