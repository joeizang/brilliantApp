using System.Globalization;
using Brilliant.Core.Progress;

namespace Brilliant.Lessons.UI;

/// <summary>Words and dates for mastery screens.</summary>
internal static class ConceptText
{
    public static string Level(ConceptLevel level) => level switch
    {
        ConceptLevel.NotStarted => "Not started",
        ConceptLevel.Seen => "Seen",
        ConceptLevel.Learning => "Learning",
        ConceptLevel.Known => "Known",
        _ => level.ToString(),
    };

    /// <summary>A day in the learner's own day (the offset of the projection time), the same shape on every machine, e.g. "Oct 13".</summary>
    public static string Day(DateTimeOffset at, TimeSpan offset) => at.ToOffset(offset).ToString("MMM d", CultureInfo.InvariantCulture);
}
