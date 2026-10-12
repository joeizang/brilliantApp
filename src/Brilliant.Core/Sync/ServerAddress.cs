namespace Brilliant.Core.Sync;

public static class ServerAddress
{
    public const int DefaultPort = 8443;

    /// <summary>
    /// Reads what a person types for the server: "192.168.1.20:8443", "https://nuc.local:8443/" or "nuc.local". A bare host gets https
    /// (the server speaks TLS with the household's own certificate, docs/https.md) and, when no port is typed, the default port 8443; a
    /// typed "https://" with no port means 443, for a server behind a proxy. Anything with a path, query, credentials, or a scheme other
    /// than https is refused, plain http included: neither app is allowed to send cleartext, so it could only fail later and confusingly.
    /// </summary>
    public static bool TryParse(string? text, out Uri address)
    {
        address = null!;
        var typed = text?.Trim();
        if (string.IsNullOrEmpty(typed)) return false;

        var hasScheme = typed.Contains("://", StringComparison.Ordinal);
        if (!hasScheme) typed = "https://" + typed;
        if (!Uri.TryCreate(typed, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(parsed.Host)) return false;
        if (parsed.AbsolutePath != "/" || parsed.Query.Length > 0 || parsed.Fragment.Length > 0 || parsed.UserInfo.Length > 0) return false;

        // Uri can't tell ":443" typed from nothing typed, so look at what was typed.
        var authority = typed[(typed.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
        var portTyped = authority.StartsWith('[') ? authority.Contains("]:") : authority.Contains(':');
        var port = portTyped || hasScheme ? parsed.Port : DefaultPort;
        address = new UriBuilder(parsed.Scheme, parsed.Host, port).Uri;
        return true;
    }
}
