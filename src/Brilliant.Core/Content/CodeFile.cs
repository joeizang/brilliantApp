using System.Text.RegularExpressions;

namespace Brilliant.Core.Content;

/// <summary>One source file of a multi-file exercise: its name (for example <c>Circle.cs</c>) and its text.</summary>
public sealed record CodeFile(string Name, string Content);

/// <summary>The rules every part of the system shares about a multi-file exercise's files.</summary>
public static partial class CodeFiles
{
    /// <summary>The name a lone file gets, and the one diagnostics use for it.</summary>
    public const string SingleFileName = "solution.cs";

    public const int MaxFiles = 8;
    public const int MaxNameLength = 32;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\.cs$")]
    private static partial Regex ValidName();

    // Names the compiler gives its own generated sources; a learner file with one would be indistinguishable in a diagnostic.
    [GeneratedRegex(@"^(reference|test-\d+)\.cs$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    /// <summary>Why <paramref name="name"/> can't name a file, or null if it can.</summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "a file needs a 'name'.";
        if (name.Length > MaxNameLength || !ValidName().IsMatch(name))
            return $"file name '{name}' must be a plain C# file name such as Circle.cs (letters, digits and underscores, ending in .cs, at most {MaxNameLength} characters).";
        if (ReservedName().IsMatch(name)) return $"file name '{name}' is reserved for the generated test harness.";
        return null;
    }

    /// <summary>Why <paramref name="files"/> can't be one exercise's files (a count, a bad or repeated name), or null if they can.</summary>
    public static string? Problem(IReadOnlyList<CodeFile> files)
    {
        if (files.Count is < 1 or > MaxFiles) return $"an exercise has between 1 and {MaxFiles} files.";
        if (files.Count == 1) return null;
        foreach (var file in files)
            if (NameProblem(file.Name) is { } why) return why;
        var repeated = files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return repeated is null ? null : $"file name '{repeated.Key}' is used twice (names are compared ignoring case).";
    }

    /// <summary>Where a multi-file step keeps the draft of one file. Names have no '/', so this never collides with another key.</summary>
    public static string DraftKey(string stepKey, string fileName) => $"{stepKey}/{fileName}";

    /// <summary>All files as one text, for places that record "the learner's code" as a single string.</summary>
    public static string Combine(IReadOnlyList<CodeFile> files) =>
        files.Count == 1 ? files[0].Content : string.Join("\n", files.Select(f => $"// ===== {f.Name} =====\n{f.Content}"));
}
