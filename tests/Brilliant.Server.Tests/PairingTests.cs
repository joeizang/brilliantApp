using System.Net;
using System.Net.Http.Json;
using Brilliant.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PairingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Health_is_open_and_reports_ok()
    {
        await using var server = await ServerHarness.StartAsync(postgres);

        var response = await server.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_valid_code_is_exchanged_for_a_device_token()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var code = await server.MintCodeAsync();

        var response = await server.CreateClient().PostAsJsonAsync("/pair", new PairRequest(code, "Joseph's MacBook"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paired = (await response.Content.ReadFromJsonAsync<PairResponse>())!;
        Assert.False(string.IsNullOrWhiteSpace(paired.Token));
        Assert.True(Guid.TryParse(paired.DeviceId, out _));
    }

    [Fact]
    public async Task The_code_is_accepted_however_it_is_typed()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var code = await server.MintCodeAsync();   // "ABCD-EFGH"

        var response = await server.CreateClient().PostAsJsonAsync("/pair", new PairRequest($"  {code.ToLowerInvariant().Replace("-", " ")} ", "Phone"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_token_is_stored_only_as_a_hash()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (_, paired) = await server.PairDeviceAsync();

        var stored = await server.WithDbAsync(db => db.Devices.Select(d => d.TokenHash).SingleAsync());

        Assert.NotEqual(paired.Token, stored);
        Assert.DoesNotContain(paired.Token, stored);
    }

    [Fact]
    public async Task A_code_works_once()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var code = await server.MintCodeAsync();
        var client = server.CreateClient();

        var first = await client.PostAsJsonAsync("/pair", new PairRequest(code, "One"));
        var second = await client.PostAsJsonAsync("/pair", new PairRequest(code, "Two"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Two_devices_racing_with_the_same_code_cannot_both_pair()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var code = await server.MintCodeAsync();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            server.CreateClient().PostAsJsonAsync("/pair", new PairRequest(code, $"Racer {i}"))));

        Assert.Equal(1, attempts.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await server.WithDbAsync(db => db.Devices.CountAsync()));
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var code = await server.MintCodeAsync();
        server.Clock.Advance(TimeSpan.FromMinutes(16));

        var response = await server.CreateClient().PostAsJsonAsync("/pair", new PairRequest(code, "Late"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_code_still_works_just_before_it_expires()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var code = await server.MintCodeAsync();
        server.Clock.Advance(TimeSpan.FromMinutes(14));

        var response = await server.CreateClient().PostAsJsonAsync("/pair", new PairRequest(code, "Just in time"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_code_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres);

        var response = await server.CreateClient().PostAsJsonAsync("/pair", new PairRequest("AAAA-BBBB", "Guesser"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("", "Name")]
    [InlineData("AAAA-BBBB", "")]
    [InlineData("AAAA-BBBB", "   ")]
    public async Task A_missing_code_or_name_is_a_bad_request(string code, string name)
    {
        await using var server = await ServerHarness.StartAsync(postgres);

        var response = await server.CreateClient().PostAsJsonAsync("/pair", new PairRequest(code, name));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Guessing_codes_is_rate_limited()
    {
        await using var server = await ServerHarness.StartAsync(postgres, attemptsPerMinute: 3);
        var client = server.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) statuses.Add((await client.PostAsJsonAsync("/pair", new PairRequest("AAAA-BBBB", "Guesser"))).StatusCode);

        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.Unauthorized, 3), HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task A_paired_device_can_mint_a_code_for_the_next_device()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (first, _) = await server.PairDeviceAsync("Mac");

        var minted = await first.PostAsync("/pairing-codes", null);
        var code = (await minted.Content.ReadFromJsonAsync<PairingCodeResponse>())!.Code;
        var second = await server.CreateClient().PostAsJsonAsync("/pair", new PairRequest(code, "Phone"));

        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }
}
