using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Brilliant.Core.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Server.Tests;

/// <summary>Runs scripts/local-ca.sh into a folder of its own.</summary>
internal sealed class IssuedCertificates : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "brilliant-tls-" + Guid.NewGuid().ToString("N"));
    public string CaDir => Path.Combine(Root, "ca");
    public string TlsDir => Path.Combine(Root, "tls");
    public string ServerPem => Path.Combine(TlsDir, "server.pem");
    public string ServerKey => Path.Combine(TlsDir, "server.key");
    public string CaCrt => Path.Combine(TlsDir, "brilliant-ca.crt");

    public static string ScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "local-ca.sh"))) return Path.Combine(dir.FullName, "scripts", "local-ca.sh");
        throw new FileNotFoundException("scripts/local-ca.sh not found above " + AppContext.BaseDirectory);
    }

    public (int ExitCode, string Output) Run(params string[] args)
    {
        var info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(ScriptPath());
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["BRILLIANT_CA_DIR"] = CaDir;
        info.Environment["BRILLIANT_TLS_DIR"] = TlsDir;
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    public IssuedCertificates Issue(params string[] names)
    {
        var (code, output) = Run(names);
        Assert.True(code == 0, output);
        return this;
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}

/// <summary>An HTTPS server on a free port, serving the certificate in <paramref name="settings"/>.</summary>
internal sealed class TlsServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public int Port { get; }

    private TlsServer(WebApplication app, int port) => (_app, Port) = (app, port);

    public static async Task<TlsServer> StartAsync(string certificatePath, string keyPath)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tls:CertificatePath"] = certificatePath, ["Tls:KeyPath"] = keyPath, ["Tls:Port"] = "0",
        });
        var tls = ServerTls.From(builder.Configuration)!;
        var certificate = ServerTls.Load(tls, TimeProvider.System);
        builder.WebHost.ConfigureKestrel(o => ServerTls.Listen(o, certificate, tls.Port));
        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TlsServer(app, new Uri(address.Replace("[::]", "localhost")).Port);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

public class IssuedCertificateTests : IClassFixture<IssuedCertificateTests.Fixture>
{
    public sealed class Fixture : IDisposable
    {
        internal IssuedCertificates Issued { get; } = new IssuedCertificates().Issue("192.168.1.20", "nuc.local");
        public void Dispose() => Issued.Dispose();
    }

    private readonly IssuedCertificates _issued;
    public IssuedCertificateTests(Fixture fixture) => _issued = fixture.Issued;

    /// <summary>What a device that has installed the household's authority does: believe certificates it issued, and nothing else.</summary>
    private static HttpClient TrustingOnly(string caPath)
    {
        var ca = X509CertificateLoader.LoadCertificateFromFile(caPath);
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null) return false;
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(new X509Certificate2(certificate));
        };
        return new HttpClient(handler);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task A_device_that_trusts_the_authority_reaches_the_server_over_https(string host)
    {
        await using var server = await TlsServer.StartAsync(_issued.ServerPem, _issued.ServerKey);
        using var client = TrustingOnly(_issued.CaCrt);

        var response = await client.GetAsync($"https://{host}:{server.Port}/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_device_that_has_not_installed_the_authority_is_refused_and_told_why()
    {
        await using var server = await TlsServer.StartAsync(_issued.ServerPem, _issued.ServerKey);
        using var client = new HttpClient();

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://127.0.0.1:{server.Port}/health"));

        Assert.True(UntrustedCertificate.Explains(e), e.ToString());
    }

    [Fact]
    public async Task The_app_clients_pair_over_https_with_the_default_trust_only_once_the_authority_is_trusted()
    {
        await using var server = await TlsServer.StartAsync(_issued.ServerPem, _issued.ServerKey);

        // Not installed: the pairing screen is told it's the certificate, not the network.
        var untrusting = await new PairingClient(new HttpClient()).PairAsync($"127.0.0.1:{server.Port}", "ABCD-2345", "Mac");
        Assert.Equal(PairingFailure.Untrusted, untrusting.Failure);

        // Installed: the request reaches the server (which here has no /pair, so it answers 404, not a handshake failure).
        var trusting = await new PairingClient(TrustingOnly(_issued.CaCrt)).PairAsync($"127.0.0.1:{server.Port}", "ABCD-2345", "Mac");
        Assert.Equal(PairingFailure.Unreachable, trusting.Failure);
        Assert.DoesNotContain("SSL", trusting.Detail ?? "");
    }

    [Fact]
    public void The_certificate_names_the_addresses_it_was_asked_for_and_the_ones_every_setup_needs()
    {
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(_issued.ServerPem);
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        var dns = san.EnumerateDnsNames().ToList();
        var ips = san.EnumerateIPAddresses().Select(a => a.ToString()).ToList();

        Assert.Contains("nuc.local", dns);
        Assert.Contains("localhost", dns);
        Assert.Contains("192.168.1.20", ips);
        Assert.Contains("127.0.0.1", ips);
        Assert.Contains("10.0.2.2", ips);   // the Android emulator's name for the machine it runs on
    }

    [Fact]
    public void The_certificate_is_one_a_server_may_present_and_not_one_that_can_issue_more()
    {
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(_issued.ServerPem);

        Assert.False(certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(usage.EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");   // serverAuth
        Assert.True(certificate.NotAfter - DateTime.Now < TimeSpan.FromDays(826), "Apple refuses longer-lived certificates");
    }

    [Fact]
    public void Issuing_again_keeps_the_authority_so_devices_need_nothing()
    {
        using var again = new IssuedCertificates();
        again.Issue("192.168.1.20");
        var ca = File.ReadAllBytes(again.CaCrt);
        var first = File.ReadAllBytes(again.ServerPem);

        again.Issue("192.168.1.99");

        Assert.Equal(ca, File.ReadAllBytes(again.CaCrt));
        Assert.NotEqual(first, File.ReadAllBytes(again.ServerPem));
        using var renewed = X509CertificateLoader.LoadCertificateFromFile(again.ServerPem);
        Assert.Contains("192.168.1.99", renewed.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateIPAddresses().Select(a => a.ToString()));
        Assert.Equal(0, again.Run("--check").ExitCode);
    }

    [Fact]
    public void The_authority_key_stays_with_the_authority_and_is_private()
    {
        Assert.True(File.Exists(Path.Combine(_issued.CaDir, "ca.key")));
        Assert.False(File.Exists(Path.Combine(_issued.TlsDir, "ca.key")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_issued.CaDir, "ca.key")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_issued.CaDir));
        }
    }

    [Fact]
    public void A_name_that_cannot_be_a_host_is_refused_before_anything_is_written()
    {
        using var fresh = new IssuedCertificates();

        var (code, output) = fresh.Run("nuc.local; touch pwned");

        Assert.NotEqual(0, code);
        Assert.Contains("not a host name", output);
        Assert.False(File.Exists(fresh.ServerPem));
    }

    [Fact]
    public void With_no_certificate_check_says_to_run_the_script()
    {
        using var fresh = new IssuedCertificates();

        var (code, output) = fresh.Run("--check");

        Assert.NotEqual(0, code);
        Assert.Contains("scripts/local-ca.sh", output);
    }
}

public class ServerTlsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "brilliant-tls-unit-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    public ServerTlsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private TlsSettings Write(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        var cert = Path.Combine(_dir, "server.pem");
        var keyFile = Path.Combine(_dir, "server.key");
        File.WriteAllText(cert, certificate.ExportCertificatePem());
        File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem());
        return new TlsSettings(cert, keyFile, 8443);
    }

    [Fact]
    public void Without_settings_the_server_stays_on_its_default_plain_endpoint()
    {
        Assert.Null(ServerTls.From(Config()));
        Assert.Null(ServerTls.From(Config(("Tls:Port", "9443"))));
    }

    [Fact]
    public void Settings_give_the_port_8443_unless_told_otherwise()
    {
        Assert.Equal(new TlsSettings("/tls/server.pem", "/tls/server.key", 8443),
            ServerTls.From(Config(("Tls:CertificatePath", "/tls/server.pem"), ("Tls:KeyPath", "/tls/server.key"))));
        Assert.Equal(9443, ServerTls.From(Config(("Tls:CertificatePath", "a"), ("Tls:KeyPath", "b"), ("Tls:Port", "9443")))!.Port);
    }

    [Theory]
    [InlineData("Tls:CertificatePath")]
    [InlineData("Tls:KeyPath")]
    public void A_certificate_without_its_key_or_the_reverse_is_a_mistake_not_a_plain_server(string only)
    {
        var e = Assert.Throws<InvalidOperationException>(() => ServerTls.From(Config((only, "/tls/x"))));
        Assert.Contains("Tls:CertificatePath", e.Message);
        Assert.Contains("Tls:KeyPath", e.Message);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("70000")]
    [InlineData("eight")]
    public void A_port_that_is_not_one_is_refused(string port) =>
        Assert.Throws<InvalidOperationException>(() => ServerTls.From(Config(("Tls:CertificatePath", "a"), ("Tls:KeyPath", "b"), ("Tls:Port", port))));

    [Fact]
    public void A_port_of_zero_is_allowed_only_to_let_tests_pick_one()
    {
        Assert.Equal(0, ServerTls.From(Config(("Tls:CertificatePath", "a"), ("Tls:KeyPath", "b"), ("Tls:Port", "0")))!.Port);
    }

    [Fact]
    public void A_missing_certificate_says_where_it_looked_and_how_to_make_one()
    {
        var e = Assert.Throws<InvalidOperationException>(() => ServerTls.Load(new TlsSettings(Path.Combine(_dir, "nope.pem"), Path.Combine(_dir, "nope.key"), 8443), TimeProvider.System));
        Assert.Contains("nope.pem", e.Message);
        Assert.Contains("scripts/local-ca.sh", e.Message);
    }

    [Fact]
    public void A_file_that_is_not_a_certificate_is_explained()
    {
        var settings = Write(Now.AddDays(-1), Now.AddDays(100));
        File.WriteAllText(settings.CertificatePath, "not a certificate");

        var e = Assert.Throws<InvalidOperationException>(() => ServerTls.Load(settings, new FixedClock(Now)));

        Assert.Contains(settings.CertificatePath, e.Message);
        Assert.Contains("scripts/local-ca.sh", e.Message);
    }

    [Fact]
    public void A_key_that_belongs_to_another_certificate_is_explained()
    {
        var settings = Write(Now.AddDays(-1), Now.AddDays(100));
        using var other = RSA.Create(2048);
        File.WriteAllText(settings.KeyPath, other.ExportPkcs8PrivateKeyPem());

        var e = Assert.Throws<InvalidOperationException>(() => ServerTls.Load(settings, new FixedClock(Now)));

        Assert.Contains("scripts/local-ca.sh", e.Message);
    }

    [Fact]
    public void An_expired_certificate_is_refused_because_every_device_would_refuse_it()
    {
        var settings = Write(Now.AddDays(-900), Now.AddDays(-1));

        var e = Assert.Throws<InvalidOperationException>(() => ServerTls.Load(settings, new FixedClock(Now)));

        Assert.Contains("expired", e.Message);
        Assert.Contains("scripts/local-ca.sh", e.Message);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(31, false)]
    [InlineData(29, true)]
    [InlineData(1, true)]
    public void A_certificate_close_to_expiry_is_flagged_so_it_can_be_renewed_in_time(int daysLeft, bool soon)
    {
        var settings = Write(Now.AddDays(-10), Now.AddDays(daysLeft));

        using var certificate = ServerTls.Load(settings, new FixedClock(Now));

        Assert.Equal(soon, ServerTls.ExpiresSoon(certificate, new FixedClock(Now)));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public class DeploymentConfigTests
{
    private static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) return dir.FullName;
        throw new FileNotFoundException("repository root not found");
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Repo(), .. path]));

    [Fact]
    public void Android_allows_no_cleartext_and_trusts_authorities_installed_on_the_phone()
    {
        var manifest = Read("src", "Brilliant.App", "Platforms", "Android", "AndroidManifest.xml");
        Assert.DoesNotContain("usesCleartextTraffic=\"true\"", manifest);
        Assert.Contains("android:networkSecurityConfig=\"@xml/network_security_config\"", manifest);

        var config = Read("src", "Brilliant.App", "Platforms", "Android", "Resources", "xml", "network_security_config.xml");
        Assert.Contains("cleartextTrafficPermitted=\"false\"", config);
        Assert.DoesNotContain("cleartextTrafficPermitted=\"true\"", config);
        Assert.Contains("<certificates src=\"user\"", config);   // without this Android ignores a CA the person installs
        Assert.Contains("<certificates src=\"system\"", config);
    }

    [Fact]
    public void The_mac_app_has_no_transport_security_exceptions()
    {
        var plist = Read("src", "Brilliant.App", "Platforms", "MacCatalyst", "Info.plist");
        Assert.DoesNotContain("NSAppTransportSecurity", plist);
        Assert.DoesNotContain("NSAllowsArbitraryLoads", plist);
        Assert.DoesNotContain("NSAllowsLocalNetworking", plist);
    }

    [Fact]
    public void The_compose_server_serves_the_certificate_the_script_makes_and_publishes_only_the_https_port()
    {
        var compose = Read("docker-compose.yml");
        Assert.Contains("Tls__CertificatePath: /tls/server.pem", compose);
        Assert.Contains("Tls__KeyPath: /tls/server.key", compose);
        Assert.Contains(":/tls:ro", compose);
        Assert.Contains("\"${SERVER_PORT:-8443}:8443\"", compose);
        Assert.DoesNotContain(":8080\"", compose);
    }

    [Fact]
    public void The_generated_keys_never_reach_git()
    {
        Assert.Contains(Read(".gitignore").Split('\n').Select(l => l.Trim()), l => l is "tls/" or "/tls/");
    }
}
