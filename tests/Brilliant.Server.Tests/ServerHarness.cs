using System.Net.Http.Headers;
using System.Net.Http.Json;
using Brilliant.Core.Apps;
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
    private readonly string? _appsDirectory;
    private readonly IReadOnlyDictionary<string, string?>? _settings;
    private readonly Action<IServiceCollection>? _configureServices;

    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));

    private ServerHarness(string connectionString, int attemptsPerMinute, string? appsDirectory, IReadOnlyDictionary<string, string?>? settings, Action<IServiceCollection>? configureServices) =>
        (_connectionString, _attemptsPerMinute, _appsDirectory, _settings, _configureServices) = (connectionString, attemptsPerMinute, appsDirectory, settings, configureServices);

    /// <param name="settings">Extra configuration, such as <c>Feedback:OllamaUrl</c>.</param>
    /// <param name="configureServices">Replaces services after the server's own are registered (a fake Ollama, say).</param>
    public static async Task<ServerHarness> StartAsync(PostgresFixture postgres, int attemptsPerMinute = 1000, string? appsDirectory = null,
        IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? configureServices = null) =>
        new(await postgres.CreateDatabaseAsync(), attemptsPerMinute, appsDirectory, settings, configureServices);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
        if (_appsDirectory is not null) builder.UseSetting("Apps:Directory", _appsDirectory);
        builder.UseSetting("Pairing:AttemptsPerMinute", _attemptsPerMinute.ToString());
        foreach (var (key, value) in _settings ?? new Dictionary<string, string?>()) builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            _configureServices?.Invoke(services);
        });
    }

    /// <summary>A pairing code, minted straight from the service the way the CLI does it.</summary>
    public async Task<string> MintCodeAsync(string appId = AppStoragePaths.DsaAppId)
    {
        using var scope = Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<PairingCodes>().MintAsync(appId)).Code;
    }

    public async Task<T> WithDbAsync<T>(Func<ServerDb, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ServerDb>());
    }

    /// <summary>Pairs a new device and returns a client that carries its token.</summary>
    public async Task<(HttpClient Client, PairResponse Device)> PairDeviceAsync(string name = "Test device", string appId = AppStoragePaths.DsaAppId)
    {
        var pairing = CreateClient();
        if (appId != AppStoragePaths.DsaAppId) pairing.DefaultRequestHeaders.Add("X-Brilliant-App-Id", appId);
        var response = await pairing.PostAsJsonAsync("/pair", new PairRequest(await MintCodeAsync(appId), name, appId));
        response.EnsureSuccessStatusCode();
        var paired = (await response.Content.ReadFromJsonAsync<PairResponse>())!;
        return (Authorised(paired.Token, appId), paired);
    }

    public HttpClient Authorised(string token, string appId = AppStoragePaths.DsaAppId)
    {
        var client = CreateClient();
        if (appId != AppStoragePaths.DsaAppId) client.DefaultRequestHeaders.Add("X-Brilliant-App-Id", appId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
