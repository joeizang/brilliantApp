using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Brilliant.Core.Apps;
using Brilliant.Core.Sync;

namespace Brilliant.Core.Tests;

public sealed class AppSyncClientTests
{
    private const string AppId = "example.csharp";
    private static readonly DeviceCredentials Credentials = new(new Uri("https://server/"), "device", "token", AppId);

    private sealed class Server(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal(AppId, Assert.Single(request.Headers.GetValues(AppSync.AppIdHeader)));
            return respond(request);
        }
    }

    [Fact]
    public async Task Pairing_sync_and_packs_carry_the_selected_app_identity()
    {
        using var server = new Server(async request =>
        {
            object response = request.RequestUri!.AbsolutePath switch
            {
                "/pair" => new PairResponse("device", "token", (await request.Content!.ReadFromJsonAsync<PairRequest>())!.AppId),
                "/events" => new PullEventsResponse([], 0, false),
                "/drafts" => new PullDraftsResponse([], 0, false),
                "/packs" => new PackListResponse([]),
                _ => throw new InvalidOperationException(),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        });
        using var http = new HttpClient(server);
        var pairing = await new PairingClient(http, AppId).PairAsync("https://server", "code", "Mac");
        Assert.Equal(Credentials, pairing.Credentials);
        await new SyncClient(http, AppId).PullAsync(pairing.Credentials!, 0);
        await new SyncClient(http, AppId).PullDraftsAsync(pairing.Credentials!, 0);
        await new PackClient(http, AppId).ListAsync(pairing.Credentials!);
        Assert.Equal(4, server.Requests);
    }

    [Fact]
    public async Task Non_dsa_pairing_does_not_accept_an_old_servers_unscoped_response()
    {
        using var server = new Server(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { deviceId = "device", token = "token" }) }));
        using var http = new HttpClient(server);
        Assert.False((await new PairingClient(http, AppId).PairAsync("https://server", "code", "Mac")).Succeeded);
    }

    [Fact]
    public async Task Every_authenticated_operation_rejects_another_apps_credentials_without_http()
    {
        using var server = new Server(_ => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(server);
        var sync = new SyncClient(http, AppId);
        var packs = new PackClient(http, AppId);
        var wrong = Credentials with { AppId = AppStoragePaths.DsaAppId };
        Func<Task>[] operations = [
            () => sync.PushAsync(wrong, []), () => sync.PullAsync(wrong, 0),
            () => sync.PushDraftsAsync(wrong, []), () => sync.PullDraftsAsync(wrong, 0),
            () => packs.ListAsync(wrong), () => packs.PublishAsync(wrong, []),
            () => packs.DownloadAsync(wrong, new PackInfo("pack.t", "1.0.0", 1, "abc", 3, DateTimeOffset.UnixEpoch, AppId))
        ];
        foreach (var operation in operations)
            Assert.Equal(SyncFailure.Unauthorized, (await Assert.ThrowsAsync<SyncException>(operation)).Failure);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task Pack_metadata_from_another_app_is_never_accepted()
    {
        var pack = new PackInfo("pack.t", "1.0.0", 1, "abc", 3, DateTimeOffset.UnixEpoch);
        using var listServer = new Server(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new PackListResponse([pack])) }));
        using var listHttp = new HttpClient(listServer);
        var client = new PackClient(listHttp, AppId);
        Assert.Equal(SyncFailure.ServerError, (await Assert.ThrowsAsync<SyncException>(() => client.ListAsync(Credentials))).Failure);
        Assert.Equal(SyncFailure.ServerError, (await Assert.ThrowsAsync<SyncException>(() => client.DownloadAsync(Credentials, pack))).Failure);
        Assert.Equal(1, listServer.Requests);
        using var publishServer = new Server(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new PublishPackResponse(pack, true, [])) }));
        using var publishHttp = new HttpClient(publishServer);
        Assert.Equal(SyncFailure.ServerError, (await Assert.ThrowsAsync<SyncException>(() =>
            new PackClient(publishHttp, AppId).PublishAsync(Credentials, []))).Failure);
    }

    [Fact]
    public async Task Existing_json_credentials_keep_dsa_identity_and_new_credentials_persist_the_app()
    {
        var path = Path.Combine(Path.GetTempPath(), "app-creds-" + Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """{"Server":"https://server/","DeviceId":"device","Token":"token"}""");
            var store = new FileDeviceCredentialStore(path);
            Assert.Equal(AppStoragePaths.DsaAppId, (await store.LoadAsync())!.AppId);
            Assert.Equal(AppStoragePaths.DsaAppId, JsonSerializer.Deserialize<DeviceCredentials>(await File.ReadAllTextAsync(path))!.AppId);
            await store.SaveAsync(Credentials);
            Assert.Equal(Credentials, await new FileDeviceCredentialStore(path).LoadAsync());
        }
        finally { File.Delete(path); }
    }
}
