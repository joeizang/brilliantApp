using System.Net.Http;
using System.Security.Authentication;

namespace Brilliant.Core.Sync;

/// <summary>
/// Tells "this device doesn't trust the server's certificate" from "the server is away". The server's certificate comes from the household's
/// own certificate authority (docs/https.md), so a device that hasn't had that authority installed fails the handshake, and the person needs
/// to be told to install it rather than to check the network.
/// </summary>
public static class UntrustedCertificate
{
    public static bool Explains(Exception e)
    {
        if (e is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }) return true;
        for (var inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException) return true;

            // Android's HTTP stack reports a failed handshake with Java's exceptions (Javax.Net.Ssl.SSLHandshakeException,
            // Java.Security.Cert.CertPathValidatorException, ...), which this project can't reference.
            var name = inner.GetType().FullName ?? "";
            if (name.StartsWith("Javax.Net.Ssl.", StringComparison.Ordinal) || name.StartsWith("Java.Security.Cert.", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
