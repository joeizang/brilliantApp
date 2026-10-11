using System.Net.Http.Headers;
using System.Net.Http.Json;
using Brilliant.Core.Sync;
using Brilliant.Server.Data;
using Brilliant.Server.Pairing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Server.Tests;

public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>The server running in-process against its own database in the shared container.</summary>
public sealed class ServerHarness : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly int _attemptsPerMinute;

    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));

    private ServerHarness(string connectionString, int attemptsPerMinute) =>
        (_connectionString, _attemptsPerMinute) = (connectionString, attemptsPerMinute);

    public static async Task<ServerHarness> StartAsync(PostgresFixture postgres, int attemptsPerMinute = 1000) =>
        new(await postgres.CreateDatabaseAsync(), attemptsPerMinute);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
        builder.UseSetting("Pairing:AttemptsPerMinute", _attemptsPerMinute.ToString());
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    /// <summary>A pairing code, minted straight from the service the way the CLI does it.</summary>
    public async Task<string> MintCodeAsync()
    {
        using var scope = Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<PairingCodes>().MintAsync()).Code;
    }

    public async Task<T> WithDbAsync<T>(Func<ServerDb, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ServerDb>());
    }

    /// <summary>Pairs a new device and returns a client that carries its token.</summary>
    public async Task<(HttpClient Client, PairResponse Device)> PairDeviceAsync(string name = "Test device")
    {
        var response = await CreateClient().PostAsJsonAsync("/pair", new PairRequest(await MintCodeAsync(), name));
        response.EnsureSuccessStatusCode();
        var paired = (await response.Content.ReadFromJsonAsync<PairResponse>())!;
        return (Authorised(paired.Token), paired);
    }

    public HttpClient Authorised(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
