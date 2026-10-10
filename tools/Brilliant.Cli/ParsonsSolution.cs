using Brilliant.Core.Content;

namespace Brilliant.Cli;

/// <summary>
/// Reads the reference ordering of a Parsons step (its <c>solution</c> block) and checks that it is consistent: every line is one piece the
/// learner places, its indentation is a whole number of levels, and the structure makes sense (code only gets deeper after a line ending in <c>:</c>,
/// and a line ending in <c>:</c> is followed by a deeper line). Blank lines are dropped.
/// </summary>
internal static class ParsonsSolution
{
    public const int MinLines = 2;

    public static List<ParsonsLine> Read(string solution, Action<string> error)
    {
        var raw = solution.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select((text, i) => (Number: i + 1, Text: text.TrimEnd()))
            .Where(l => l.Text.Length > 0)
            .ToList();

        if (raw.Any(l => l.Text.Contains('\t')))
        {
            error($"'solution' line {raw.First(l => l.Text.Contains('\t')).Number} uses a tab; indent with spaces.");
            return [];
        }

        var indents = raw.Select(l => (l.Number, l.Text, Indent: l.Text.Length - l.Text.TrimStart().Length)).ToList();
        var unit = indents.Where(l => l.Indent > 0).Select(l => l.Indent).DefaultIfEmpty(0).Min();
        var ok = true;
        foreach (var l in indents.Where(l => unit > 0 && l.Indent % unit != 0))
        {
            error($"'solution' line {l.Number} is indented {l.Indent} spaces, which isn't a multiple of the indent used elsewhere ({unit}).");
            ok = false;
        }
        if (!ok) return [];

        var lines = indents.Select(l => new ParsonsLine(l.Text.Trim(), unit == 0 ? 0 : l.Indent / unit)).ToList();
        var numbers = indents.Select(l => l.Number).ToList();

        if (lines.Count < MinLines)
        {
            error($"a Parsons step needs at least {MinLines} lines in 'solution'.");
            return lines;
        }
        if (lines[0].Level != 0) error($"'solution' line {numbers[0]} is indented, but the first line must not be.");

        for (var i = 1; i < lines.Count; i++)
        {
            var opens = OpensBlock(lines[i - 1].Text);
            if (opens && lines[i].Level != lines[i - 1].Level + 1)
                error($"'solution' line {numbers[i - 1]} ends with ':' so line {numbers[i]} must be indented one level deeper.");
            else if (!opens && lines[i].Level > lines[i - 1].Level)
                error($"'solution' line {numbers[i]} is indented deeper than the line before it, which doesn't end with ':'.");
        }
        if (OpensBlock(lines[^1].Text)) error($"'solution' ends with ':' on line {numbers[^1]} but nothing follows it.");
        if (lines.Select(l => l.Text).Distinct().Count() < 2) error("'solution' has only one distinct line, so there is nothing to put in order.");
        return lines;
    }

    // A line opens a block when it ends with ':' once any trailing comment is ignored, so `if ok:  # note` counts and `x = 1  # why:` doesn't.
    // The '#' only starts a comment outside a string literal.
    internal static bool OpensBlock(string line)
    {
        char? quote = null;
        var end = line.Length;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is not null)
            {
                if (c == '\\') i++;
                else if (c == quote) quote = null;
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '#') { end = i; break; }
        }
        return line[..end].TrimEnd().EndsWith(':');
    }
}
