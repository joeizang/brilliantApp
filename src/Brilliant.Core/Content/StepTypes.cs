using System.Collections.Frozen;

namespace Brilliant.Core.Content;

/// <summary>The closed set of content step keys supported by the engine. Keep in sync with Step's JSON discriminators.</summary>
public static class StepTypes
{
    public static IReadOnlySet<string> All { get; } = new[]
    {
        "explain", "choice", "predict-output", "write-code", "fill-blank", "parsons", "trace", "lowered",
    }.ToFrozenSet(StringComparer.Ordinal);
}
