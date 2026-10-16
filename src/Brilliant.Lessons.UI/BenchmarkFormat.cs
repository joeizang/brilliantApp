using System.Globalization;

namespace Brilliant.Lessons.UI;

/// <summary>How a benchmark's numbers read to a learner: a time in the unit that keeps it short, bytes the same way.</summary>
public static class BenchmarkFormat
{
    public static string Time(double ns) => ns switch
    {
        >= 1_000_000 => $"{(ns / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture)} ms",
        >= 1_000 => $"{(ns / 1_000).ToString("0.##", CultureInfo.InvariantCulture)} µs",
        _ => $"{ns.ToString("0.##", CultureInfo.InvariantCulture)} ns",
    };

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{(bytes / 1_048_576.0).ToString("0.##", CultureInfo.InvariantCulture)} MB",
        >= 1_024 => $"{(bytes / 1_024.0).ToString("0.##", CultureInfo.InvariantCulture)} KB",
        _ => $"{bytes} B",
    };
}
