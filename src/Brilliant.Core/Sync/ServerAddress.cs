namespace Brilliant.Core.Sync;

public static class ServerAddress
{
    public const int DefaultPort = 8080;

    /// <summary>
    /// Reads what a person types for the server: "192.168.1.20:8080", "http://nuc.local:8080/" or "nuc.local". A bare host gets http
    /// (the server is plain HTTP on the home network) and, when no port is typed, the default port 8080. Anything with a path, query,
    /// credentials or a scheme other than http/https is refused.
    /// </summary>
    public static bool TryParse(string? text, out Uri address)
    {
        address = null!;
        var typed = text?.Trim();
        if (string.IsNullOrEmpty(typed)) return false;

        var hasScheme = typed.Contains("://", StringComparison.Ordinal);
        if (!hasScheme) typed = "http://" + typed;
        if (!Uri.TryCreate(typed, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host)) return false;
        if (parsed.AbsolutePath != "/" || parsed.Query.Length > 0 || parsed.Fragment.Length > 0 || parsed.UserInfo.Length > 0) return false;

        // Uri can't tell ":80" typed from nothing typed, so look at what was typed.
        var authority = typed[(typed.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
        var portTyped = authority.StartsWith('[') ? authority.Contains("]:") : authority.Contains(':');
        var port = portTyped || parsed.Scheme == "https" ? parsed.Port : DefaultPort;
        address = new UriBuilder(parsed.Scheme, parsed.Host, port).Uri;
        return true;
    }
}
