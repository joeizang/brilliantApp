using System.Text.RegularExpressions;

namespace Brilliant.Core.Content;

/// <summary>A piece of one template line: literal code (<see cref="Text"/>) or a blank (<see cref="BlankId"/>).</summary>
public sealed record TemplatePart(string? Text, string? BlankId)
{
    public static TemplatePart Code(string text) => new(text, null);
    public static TemplatePart Blank(string id) => new(null, id);
}

/// <summary>
/// The template syntax of <see cref="FillBlankStep"/>: <c>{{blank-id}}</c> marks a blank, where the id is lowercase words
/// joined by <c>-</c>. Everything else is literal code. Pure parsing, shared by the validator and the lesson UI.
/// </summary>
public static partial class FillBlankTemplate
{
    [GeneratedRegex(@"^[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\{\{([^{}\r\n]*)\}\}")]
    private static partial Regex Marker();

    public static bool IsValidBlankId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>Every <c>{{…}}</c> in the template, in order, exactly as written between the braces (valid or not).</summary>
    public static IReadOnlyList<string> Markers(string template) =>
        Marker().Matches(template).Select(m => m.Groups[1].Value).ToList();

    /// <summary>The template split into lines, each a sequence of code and blank parts. A well-formed marker becomes a blank part; anything else stays literal.</summary>
    public static IReadOnlyList<IReadOnlyList<TemplatePart>> Lines(string template) =>
        template.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n').Select(ParseLine).ToList();

    private static IReadOnlyList<TemplatePart> ParseLine(string line)
    {
        var parts = new List<TemplatePart>();
        var position = 0;
        foreach (Match m in Marker().Matches(line))
        {
            if (!IsValidBlankId(m.Groups[1].Value)) continue;
            if (m.Index > position) parts.Add(TemplatePart.Code(line[position..m.Index]));
            parts.Add(TemplatePart.Blank(m.Groups[1].Value));
            position = m.Index + m.Length;
        }
        if (position < line.Length) parts.Add(TemplatePart.Code(line[position..]));
        return parts;
    }

    /// <summary>The template with every blank replaced by <paramref name="fill"/>(blank id), e.g. to show the finished code.</summary>
    public static string Fill(string template, Func<string, string> fill) =>
        Marker().Replace(template, m => IsValidBlankId(m.Groups[1].Value) ? fill(m.Groups[1].Value) : m.Value);
}
