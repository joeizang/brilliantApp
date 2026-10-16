using Brilliant.Core.Apps;
using Brilliant.Core.Content;
using Brilliant.Core.Drafts;
using Brilliant.Core.Progress;
using Brilliant.Core.Sync;

namespace Brilliant.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AppClientIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_catalog_with_only_a_non_dsa_app_starts_and_can_pair()
    {
        var root = Path.Combine(Path.GetTempPath(), "brilliant-server-app-catalog-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "apps", "runtime-check", "manifest.yaml"), Path.Combine(root, "manifest.yaml"));
            await using var server = await ServerHarness.StartAsync(postgres, appsDirectory: root);
            using var http = server.CreateClient();
            Assert.True((await http.GetAsync("/health")).IsSuccessStatusCode);
            const string appId = "com.joeizang.brilliantapp.runtimecheck";
            var outcome = await new PairingClient(http, appId).PairAsync("https://localhost", await server.MintCodeAsync(appId), "Same Mac");
            Assert.True(outcome.Succeeded);
            Assert.Equal(appId, outcome.Credentials!.AppId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Real_clients_pair_sync_and_publish_independently_on_the_same_machine()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var http = new HttpClient(server.Server.CreateHandler());
        string[] apps = [AppStoragePaths.DsaAppId, "com.joeizang.brilliantapp.runtimecheck"];
        var devices = new List<DeviceCredentials>();
        foreach (var appId in apps)
        {
            var outcome = await new PairingClient(http, appId).PairAsync("https://localhost", await server.MintCodeAsync(appId), "Same Mac");
            Assert.True(outcome.Succeeded);
            var device = outcome.Credentials!;
            Assert.Equal(appId, device.AppId);
            devices.Add(device);

            var sync = new SyncClient(http, appId);
            var result = await sync.PushAsync(device,
                [new("event.same", "device.same", server.Clock.GetUtcNow(), "LessonCompleted", "lesson.same", "step.same", appId)]);
            Assert.Equal(1, result.Accepted);
            var drafts = await sync.PushDraftsAsync(device, [new CodeDraft("step.same", appId, server.Clock.GetUtcNow())]);
            Assert.Equal(1, drafts.Accepted);

            using var stream = new MemoryStream();
            ContentPackFormat.Write(stream, new("pack.same", "1.0.0", ContentPackFormat.CurrentFormatVersion),
                [new("track.same", "Same", ["lesson.same"])], [new("lesson.same", "Same", "track.same", [new ExplainStep("step.same", "Same", appId, [], null)])]);
            var published = await new PackClient(http, appId).PublishAsync(device, stream.ToArray());
            Assert.True(published.Created);
            Assert.Equal(appId, published.Pack.AppId);
        }

        for (var i = 0; i < apps.Length; i++)
        {
            var appId = apps[i];
            var device = devices[i];
            var sync = new SyncClient(http, appId);
            Assert.Equal(appId, Assert.Single((await sync.PullAsync(device, 0)).Events).Data);
            Assert.Equal(appId, Assert.Single((await sync.PullDraftsAsync(device, 0)).Drafts).Code);
            var packs = new PackClient(http, appId);
            var pack = Assert.Single(await packs.ListAsync(device));
            var bytes = await packs.DownloadAsync(device, pack);
            var content = ContentPackFormat.Load(new MemoryStream(bytes));
            Assert.Equal(appId, Assert.IsType<ExplainStep>(content.Get<Step>("step.same")).Body);
        }
    }
}
