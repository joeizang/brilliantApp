namespace Brilliant.Core.Content;

/// <summary>A pack's version, <c>major.minor.patch</c>, compared number by number (so 1.10.0 is newer than 1.9.0).</summary>
public readonly record struct PackVersion(int Major, int Minor, int Patch) : IComparable<PackVersion>
{
    public static bool TryParse(string? text, out PackVersion version)
    {
        version = default;
        var parts = text?.Split('.');
        if (parts is not { Length: 3 }) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            // Plain digits only: no sign, no spaces, and nothing so long it overflows.
            if (parts[i].Length is 0 or > 9 || !parts[i].All(char.IsAsciiDigit)) return false;
            numbers[i] = int.Parse(parts[i]);
        }
        version = new PackVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public static PackVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a version like 1.2.3.");

    public int CompareTo(PackVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);

    public static bool operator <(PackVersion a, PackVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(PackVersion a, PackVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(PackVersion a, PackVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(PackVersion a, PackVersion b) => a.CompareTo(b) >= 0;

    /// <summary>The normalised form (<c>1.2.3</c>, without leading zeros), which is how the server stores and lists it.</summary>
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>Pack IDs end up in URLs and file names, so they are held to a small alphabet. The server and the app both enforce it.</summary>
public static class PackIds
{
    public const int MaxLength = 100;

    public static bool IsValid(string? id) =>
        id is { Length: > 0 and <= MaxLength } && id[0] != '.' && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
