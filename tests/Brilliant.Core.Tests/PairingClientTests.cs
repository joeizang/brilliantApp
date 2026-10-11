using System.Net;
using System.Net.Http.Json;
using Brilliant.Core.Sync;

namespace Brilliant.Core.Tests;

public class ServerAddressTests
{
    [Theory]
    [InlineData("192.168.1.20:9000", "http://192.168.1.20:9000/")]
    [InlineData("nuc.local", "http://nuc.local:8080/")]
    [InlineData("  nuc.local:8080/  ", "http://nuc.local:8080/")]
    [InlineData("http://nuc.local", "http://nuc.local:8080/")]
    [InlineData("http://nuc.local:80", "http://nuc.local/")]
    [InlineData("https://learn.example.com", "https://learn.example.com/")]
    [InlineData("https://learn.example.com:8443/", "https://learn.example.com:8443/")]
    [InlineData("[fe80::1]:8080", "http://[fe80::1]:8080/")]
    public void Addresses_people_type_are_understood(string typed, string expected)
    {
        Assert.True(ServerAddress.TryParse(typed, out var address));
        Assert.Equal(expected, address.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://nuc.local")]
    [InlineData("http://nuc.local/api")]
    [InlineData("http://nuc.local/?x=1")]
    [InlineData("http://user:pw@nuc.local")]
    [InlineData("http://")]
    public void Addresses_we_cannot_use_are_refused(string? typed) => Assert.False(ServerAddress.TryParse(typed, out _));
}

public class PairingClientTests
{
    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return respond(request);
        }
    }

    private sealed class MemoryStore : IDeviceCredentialStore
    {
        public DeviceCredentials? Saved;
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) { Saved = credentials; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct = default) { Saved = null; return Task.CompletedTask; }
    }

    private static (PairingClient Client, Stub Handler) ClientAnswering(HttpStatusCode status, object? body = null) =>
        Answer(_ => Task.FromResult(new HttpResponseMessage(status) { Content = body is null ? null : JsonContent.Create(body) }));

    private static (PairingClient Client, Stub Handler) Answer(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new Stub(respond);
        return (new PairingClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task A_good_code_gives_credentials_for_the_server_that_was_typed()
    {
        var (client, handler) = ClientAnswering(HttpStatusCode.OK, new PairResponse("dev-1", "tok-1"));

        var outcome = await client.PairAsync("nuc.local", "abcd-2345", "My Mac");

        Assert.True(outcome.Succeeded);
        Assert.Equal(new DeviceCredentials(new Uri("http://nuc.local:8080/"), "dev-1", "tok-1"), outcome.Credentials);
        Assert.Equal("http://nuc.local:8080/pair", handler.Seen!.RequestUri!.ToString());
        var sent = await handler.Seen.Content!.ReadFromJsonAsync<PairRequest>();
        Assert.Equal(new PairRequest("abcd-2345", "My Mac"), sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PairingFailure.CodeRejected)]
    [InlineData(HttpStatusCode.TooManyRequests, PairingFailure.TooManyAttempts)]
    [InlineData(HttpStatusCode.BadRequest, PairingFailure.Rejected)]
    [InlineData(HttpStatusCode.NotFound, PairingFailure.Unreachable)]
    [InlineData(HttpStatusCode.InternalServerError, PairingFailure.Unreachable)]
    public async Task The_servers_answer_becomes_a_failure_the_screen_can_explain(HttpStatusCode status, PairingFailure expected)
    {
        var (client, _) = ClientAnswering(status);
        var outcome = await client.PairAsync("nuc.local", "ABCD-2345", "My Mac");
        Assert.False(outcome.Succeeded);
        Assert.Equal(expected, outcome.Failure);
    }

    [Fact]
    public async Task A_200_that_is_not_a_pair_response_is_not_a_brilliant_server()
    {
        var (client, _) = ClientAnswering(HttpStatusCode.OK, new { hello = "world" });
        Assert.Equal(PairingFailure.Unreachable, (await client.PairAsync("nuc.local", "ABCD-2345", "My Mac")).Failure);
    }

    [Fact]
    public async Task A_bad_address_is_refused_without_calling_anything()
    {
        var (client, handler) = ClientAnswering(HttpStatusCode.OK);
        var outcome = await client.PairAsync("ftp://nope", "ABCD-2345", "My Mac");
        Assert.Equal(PairingFailure.BadAddress, outcome.Failure);
        Assert.Null(handler.Seen);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_reported_not_thrown()
    {
        var (client, _) = Answer(_ => throw new HttpRequestException("no route to host"));
        Assert.Equal(PairingFailure.Unreachable, (await client.PairAsync("nuc.local", "ABCD-2345", "My Mac")).Failure);
    }

    [Fact]
    public async Task Cancelling_is_not_reported_as_unreachable()
    {
        using var cts = new CancellationTokenSource();
        var (client, _) = Answer(_ => { cts.Cancel(); throw new TaskCanceledException(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PairAsync("nuc.local", "ABCD-2345", "My Mac", cts.Token));
    }

    [Fact]
    public async Task Pairing_remembers_the_credentials_and_forgetting_clears_them()
    {
        var (client, _) = ClientAnswering(HttpStatusCode.OK, new PairResponse("dev-1", "tok-1"));
        var store = new MemoryStore();
        var service = new PairingService(client, store);

        Assert.Null(await service.CurrentAsync());
        await service.PairAsync("nuc.local", "ABCD-2345", "My Mac");
        Assert.Equal("tok-1", (await service.CurrentAsync())!.Token);

        await service.ForgetAsync();
        Assert.Null(await service.CurrentAsync());
    }

    [Fact]
    public async Task A_failed_pairing_leaves_an_existing_pairing_alone()
    {
        var existing = new DeviceCredentials(new Uri("http://nuc.local:8080/"), "dev-0", "tok-0");
        var (client, _) = ClientAnswering(HttpStatusCode.Unauthorized);
        var store = new MemoryStore { Saved = existing };

        await new PairingService(client, store).PairAsync("nuc.local", "WRONG-CODE", "My Mac");

        Assert.Equal(existing, store.Saved);
    }
}
