using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Brilliant.Server;

/// <summary>Where the server's certificate and key are, and the port it serves HTTPS on.</summary>
public sealed record TlsSettings(string CertificatePath, string KeyPath, int Port);

/// <summary>
/// The server speaks HTTPS with a certificate from the household's own certificate authority (docs/https.md), made by
/// scripts/local-ca.sh and mounted into the container. Without <c>Tls:CertificatePath</c> and <c>Tls:KeyPath</c> the server keeps the
/// default plain endpoint, which is how it runs under <c>dotnet run</c> and in tests; docker-compose.yml always sets them.
/// </summary>
public static class ServerTls
{
    public const int DefaultPort = 8443;
    private const string Fix = "Run scripts/local-ca.sh on the host and make sure its tls folder is mounted at /tls (docs/https.md).";

    /// <summary>The settings, or null when TLS isn't configured. One of the two paths without the other is a mistake, not a plain server.</summary>
    public static TlsSettings? From(IConfiguration configuration)
    {
        var certificate = configuration["Tls:CertificatePath"];
        var key = configuration["Tls:KeyPath"];
        if (string.IsNullOrWhiteSpace(certificate) && string.IsNullOrWhiteSpace(key)) return null;
        if (string.IsNullOrWhiteSpace(certificate) || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Tls:CertificatePath and Tls:KeyPath must be set together, or neither.");

        var port = DefaultPort;
        if (configuration["Tls:Port"] is { Length: > 0 } text && (!int.TryParse(text, out port) || port is < 0 or > 65535))
            throw new InvalidOperationException($"Tls:Port must be a port number (got '{text}').");   // 0 lets the system choose, which tests use
        return new TlsSettings(certificate, key, port);
    }

    /// <summary>Loads the certificate, refusing one a device would refuse anyway, with a message that says what to do.</summary>
    public static X509Certificate2 Load(TlsSettings settings, TimeProvider clock)
    {
        if (!File.Exists(settings.CertificatePath)) throw new InvalidOperationException($"No TLS certificate at {settings.CertificatePath}. {Fix}");
        if (!File.Exists(settings.KeyPath)) throw new InvalidOperationException($"No TLS key at {settings.KeyPath}. {Fix}");

        X509Certificate2 certificate;
        try { certificate = X509Certificate2.CreateFromPemFile(settings.CertificatePath, settings.KeyPath); }
        catch (CryptographicException e)
        {
            throw new InvalidOperationException($"{settings.CertificatePath} and {settings.KeyPath} are not a matching PEM certificate and key ({e.Message}). {Fix}", e);
        }

        var now = clock.GetUtcNow();
        var (notBefore, notAfter) = (certificate.NotBefore, certificate.NotAfter);
        if (new DateTimeOffset(notAfter) <= now)
        {
            certificate.Dispose();
            throw new InvalidOperationException($"The TLS certificate in {settings.CertificatePath} expired on {notAfter:yyyy-MM-dd}; devices would refuse it. {Fix}");
        }
        if (new DateTimeOffset(notBefore) > now)
        {
            certificate.Dispose();
            throw new InvalidOperationException($"The TLS certificate in {settings.CertificatePath} is not valid until {notBefore:yyyy-MM-dd}; check this machine's clock.");
        }
        return certificate;
    }

    /// <summary>Whether the certificate runs out within a month, which is when to run scripts/local-ca.sh again.</summary>
    public static bool ExpiresSoon(X509Certificate2 certificate, TimeProvider clock) =>
        new DateTimeOffset(certificate.NotAfter) - clock.GetUtcNow() < TimeSpan.FromDays(30);

    /// <summary>HTTPS on every address, on the configured port, and nothing else.</summary>
    public static void Listen(KestrelServerOptions options, X509Certificate2 certificate, int port) =>
        options.ListenAnyIP(port, listen => listen.UseHttps(certificate));
}
