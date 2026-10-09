namespace Brilliant.Lessons.UI;

/// <summary>A message shown against a line of the editor (e.g. the line a traceback points at). Lines are 1-based.</summary>
public sealed record EditorMarker(int Line, string Message);

/// <summary>
/// The code editor as the lesson UI sees it. The implementation (<see cref="CodeEditor"/>) is CodeMirror 6,
/// but nothing outside that component depends on it.
/// </summary>
public interface ICodeEditor
{
    ValueTask<string> GetCodeAsync();

    ValueTask SetCodeAsync(string code);

    /// <summary>Replaces all error markers with <paramref name="markers"/> (red gutter mark, underline and message).</summary>
    ValueTask SetErrorMarkersAsync(IReadOnlyList<EditorMarker> markers);

    ValueTask ClearErrorMarkersAsync();

    /// <summary>Highlights one line and scrolls it into view; null removes the highlight.</summary>
    ValueTask HighlightLineAsync(int? line);
}
