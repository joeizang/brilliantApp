using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using Brilliant.Core.Sync;

namespace Javax.Net.Ssl
{
    // Android's HTTP stack reports a certificate it doesn't trust with Java's own exception types, which Core can't reference.
    // This stands in for one so the test can check they are recognised by name.
    public sealed class SSLHandshakeException(string message) : Exception(message);
}

namespace Brilliant.Core.Tests
{
    public class UntrustedCertificateTests
    {
        public static TheoryData<Exception, bool> Cases => new()
        {
            { new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established"), true },
            { new HttpRequestException("handshake failed", new AuthenticationException("The remote certificate is invalid")), true },
            { new HttpRequestException("handshake failed", new Javax.Net.Ssl.SSLHandshakeException("Trust anchor for certification path not found.")), true },
            { new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused"), false },
            { new HttpRequestException("no route to host", new System.Net.Sockets.SocketException(65)), false },
            { new HttpRequestException("plain"), false },
            { new TaskCanceledException(), false },
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public void A_failed_handshake_is_told_apart_from_a_server_that_is_away(Exception e, bool untrusted) =>
            Assert.Equal(untrusted, UntrustedCertificate.Explains(e));

        private static HttpRequestException Handshake() => new(HttpRequestError.SecureConnectionError, "The SSL connection could not be established");

        private sealed class Throwing(Exception e) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw e;
        }

        private static readonly DeviceCredentials Paired = new(new Uri("https://nuc.local:8443/"), "dev", "tok");

        [Fact]
        public async Task Pairing_says_the_certificate_is_not_trusted()
        {
            var client = new PairingClient(new HttpClient(new Throwing(Handshake())));
            var outcome = await client.PairAsync("nuc.local", "ABCD-2345", "My Mac");
            Assert.Equal(PairingFailure.Untrusted, outcome.Failure);
        }

        [Fact]
        public async Task Syncing_says_the_certificate_is_not_trusted()
        {
            var client = new SyncClient(new HttpClient(new Throwing(Handshake())));
            var e = await Assert.ThrowsAsync<SyncException>(() => client.PullAsync(Paired, 0));
            Assert.Equal(SyncFailure.Untrusted, e.Failure);
        }

        [Fact]
        public async Task Fetching_packs_says_the_certificate_is_not_trusted()
        {
            var client = new PackClient(new HttpClient(new Throwing(Handshake())));
            var e = await Assert.ThrowsAsync<SyncException>(() => client.ListAsync(Paired));
            Assert.Equal(SyncFailure.Untrusted, e.Failure);
        }

        [Fact]
        public async Task A_server_that_is_simply_away_is_still_unreachable()
        {
            var client = new PairingClient(new HttpClient(new Throwing(new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused"))));
            Assert.Equal(PairingFailure.Unreachable, (await client.PairAsync("nuc.local", "ABCD-2345", "My Mac")).Failure);
        }
    }
}
