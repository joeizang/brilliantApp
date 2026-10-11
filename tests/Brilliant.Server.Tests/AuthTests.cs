using System.Net;
using System.Net.Http.Json;
using Brilliant.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AuthTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("GET", "/devices")]
    [InlineData("POST", "/pairing-codes")]
    [InlineData("DELETE", "/devices/6f8c0d9e-2b1a-4c3d-8e5f-0a1b2c3d4e5f")]
    public async Task Requests_without_a_token_are_rejected(string method, string path)
    {
        await using var server = await ServerHarness.StartAsync(postgres);

        var response = await server.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_the_server_never_issued_is_rejected()
    {
        await using var server = await ServerHarness.StartAsync(postgres);

        var response = await server.Authorised("not-a-real-token").GetAsync("/devices");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_paired_device_is_let_in()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, paired) = await server.PairDeviceAsync("Joseph's MacBook");

        var response = await client.GetAsync("/devices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var devices = (await response.Content.ReadFromJsonAsync<List<DeviceInfo>>())!;
        var me = Assert.Single(devices);
        Assert.Equal(paired.DeviceId, me.Id);
        Assert.Equal("Joseph's MacBook", me.Name);
        Assert.False(me.Revoked);
        Assert.NotNull(me.LastSeenAt);   // this very request counts
    }

    [Fact]
    public async Task A_revoked_device_is_rejected_from_then_on()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (mac, _) = await server.PairDeviceAsync("Mac");
        var (phone, phoneDevice) = await server.PairDeviceAsync("Phone");

        var revoke = await mac.DeleteAsync($"/devices/{phoneDevice.DeviceId}");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mac.GetAsync("/devices")).StatusCode);   // the others carry on
    }

    [Fact]
    public async Task A_device_can_revoke_itself()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, paired) = await server.PairDeviceAsync();

        await client.DeleteAsync($"/devices/{paired.DeviceId}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/devices")).StatusCode);
    }

    [Fact]
    public async Task Revoked_devices_still_show_in_the_list_marked_as_revoked()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (mac, _) = await server.PairDeviceAsync("Mac");
        var (_, phone) = await server.PairDeviceAsync("Phone");
        await mac.DeleteAsync($"/devices/{phone.DeviceId}");

        var devices = (await (await mac.GetAsync("/devices")).Content.ReadFromJsonAsync<List<DeviceInfo>>())!;

        Assert.Equal(["Mac", "Phone"], devices.Select(d => d.Name));
        Assert.Equal([false, true], devices.Select(d => d.Revoked));
    }

    [Fact]
    public async Task Revoking_twice_is_harmless_and_an_unknown_device_is_not_found()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (mac, _) = await server.PairDeviceAsync("Mac");
        var (_, phone) = await server.PairDeviceAsync("Phone");

        Assert.Equal(HttpStatusCode.NoContent, (await mac.DeleteAsync($"/devices/{phone.DeviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await mac.DeleteAsync($"/devices/{phone.DeviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mac.DeleteAsync($"/devices/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_device_cannot_pair_again_with_its_old_token_but_a_new_code_makes_it_a_new_device()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (mac, _) = await server.PairDeviceAsync("Mac");
        var (_, phone) = await server.PairDeviceAsync("Phone");
        await mac.DeleteAsync($"/devices/{phone.DeviceId}");

        var (again, repaired) = await server.PairDeviceAsync("Phone");

        Assert.NotEqual(phone.DeviceId, repaired.DeviceId);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/devices")).StatusCode);
    }

    [Fact]
    public async Task Last_seen_is_recorded_but_not_on_every_request()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        await client.GetAsync("/devices");
        var first = await server.WithDbAsync(db => db.Devices.Select(d => d.LastSeenAt).SingleAsync());
        server.Clock.Advance(TimeSpan.FromSeconds(10));
        await client.GetAsync("/devices");
        var soon = await server.WithDbAsync(db => db.Devices.Select(d => d.LastSeenAt).SingleAsync());
        server.Clock.Advance(TimeSpan.FromMinutes(2));
        await client.GetAsync("/devices");
        var later = await server.WithDbAsync(db => db.Devices.Select(d => d.LastSeenAt).SingleAsync());

        Assert.NotNull(first);
        Assert.Equal(first, soon);
        Assert.True(later > first);
    }
}
