using System.Net.Http.Json;
using Brilliant.Cli;
using Brilliant.Core.Apps;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CSharpAppTests(PostgresFixture postgres)
{
    private const string CSharpAppId = "com.joeizang.brilliantapp.csharp";

    [Fact]
    public async Task The_actual_csharp_starter_pairs_publishes_and_syncs_without_changing_the_dsa_app()
    {
        var manifestPath = FindCSharpManifest();
        var source = AppContentSource.FromManifest(manifestPath);
        Assert.Equal(CSharpAppId, source.App.AppId);
        Assert.Equal(["csharp"], source.App.Runtimes);
        var bytes = ContentPacker.PackToBytes(source.Root, out var report, appManifestPath: manifestPath);
        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        Assert.NotNull(bytes);
        using var stream = new MemoryStream(bytes);
        var starter = ContentPackFormat.Load(stream);
        Assert.Equal(("pack.csharp.starter", "0.6.0"), (starter.Manifest.PackId, starter.Manifest.Version));
        Assert.Equal(("net10.0", "14"), (starter.Manifest.TargetFramework, starter.Manifest.LangVersion));
        var lesson = starter.Lessons.First();
        Assert.NotEmpty(lesson.Steps);

        await using var server = await ServerHarness.StartAsync(postgres);
        Assert.True(server.Services.GetRequiredService<ServerApps>().Contains(CSharpAppId));
        using var http = new HttpClient(server.Server.CreateHandler());
        var csharp = await Pair(server, http, CSharpAppId, "Same Mac");
        var dsa = await Pair(server, http, AppStoragePaths.DsaAppId, "Same Mac");
        var csharpPhone = await Pair(server, http, CSharpAppId, "Phone");
        Assert.NotEqual(csharp.DeviceId, dsa.DeviceId);
        Assert.NotEqual(csharp.Token, dsa.Token);
        Assert.Equal(CSharpAppId, csharp.AppId);
        Assert.Equal(AppStoragePaths.DsaAppId, dsa.AppId);
        var dsaDevices = (await server.Authorised(dsa.Token).GetFromJsonAsync<List<DeviceInfo>>("/devices"))!;
        Assert.Equal(dsa.DeviceId, Assert.Single(dsaDevices).Id);
        var csharpDevices = (await server.Authorised(csharp.Token, CSharpAppId).GetFromJsonAsync<List<DeviceInfo>>("/devices"))!;
        Assert.Equal(2, csharpDevices.Count);
        Assert.All(csharpDevices, device => Assert.Equal(CSharpAppId, device.AppId));

        var csharpPacks = new PackClient(http, CSharpAppId);
        var dsaPacks = new PackClient(http);
        var published = await csharpPacks.PublishAsync(csharp, bytes);
        Assert.True(published.Created);
        Assert.Equal(CSharpAppId, published.Pack.AppId);
        Assert.Empty(await dsaPacks.ListAsync(dsa));
        var dsaBytes = DsaPack();
        Assert.True((await dsaPacks.PublishAsync(dsa, dsaBytes)).Created);
        var csharpInfo = Assert.Single(await csharpPacks.ListAsync(csharpPhone));
        var dsaInfo = Assert.Single(await dsaPacks.ListAsync(dsa));
        Assert.Equal("pack.csharp.starter", csharpInfo.PackId);
        Assert.Equal("pack.dsa.test", dsaInfo.PackId);
        Assert.Equal(bytes, await csharpPacks.DownloadAsync(csharpPhone, csharpInfo));
        Assert.Equal(dsaBytes, await dsaPacks.DownloadAsync(dsa, dsaInfo));

        var csharpSync = new SyncClient(http, CSharpAppId);
        var dsaSync = new SyncClient(http);
        var completed = new ProgressEvent("lesson.completed.same.id", csharp.DeviceId, server.Clock.GetUtcNow(),
            ProgressEventTypes.LessonCompleted, lesson.Id, "");
        Assert.Equal(1, (await csharpSync.PushAsync(csharp, [completed])).Accepted);
        Assert.Equal(completed, Assert.Single((await csharpSync.PullAsync(csharpPhone, 0)).Events));
        Assert.Empty((await dsaSync.PullAsync(dsa, 0)).Events);
        var dsaCompleted = completed with { DeviceId = dsa.DeviceId, LessonId = "lesson.dsa.test" };
        Assert.Equal(1, (await dsaSync.PushAsync(dsa, [dsaCompleted])).Accepted);
        Assert.Equal(dsaCompleted, Assert.Single((await dsaSync.PullAsync(dsa, 0)).Events));
        Assert.Equal(completed, Assert.Single((await csharpSync.PullAsync(csharpPhone, 0)).Events));
    }

    private static async Task<DeviceCredentials> Pair(ServerHarness server, HttpClient http, string appId, string deviceName)
    {
        var outcome = await new PairingClient(http, appId).PairAsync("https://localhost", await server.MintCodeAsync(appId), deviceName);
        Assert.True(outcome.Succeeded, $"{appId}: {outcome.Failure}: {outcome.Detail}");
        return outcome.Credentials!;
    }

    private static string FindCSharpManifest()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "apps", "csharp", "manifest.yaml");
            if (File.Exists(path) && Directory.Exists(Path.Combine(directory.FullName, "content", "csharp"))) return path;
        }
        throw new FileNotFoundException("Could not find the repository's apps/csharp/manifest.yaml and content/csharp.");
    }

    private static byte[] DsaPack()
    {
        var lesson = new Lesson("lesson.dsa.test", "DSA test", "track.dsa.test", [new ExplainStep("step.dsa.test", "DSA test", "DSA content", [], null)]);
        using var stream = new MemoryStream();
        ContentPackFormat.Write(stream, new PackManifest("pack.dsa.test", "0.1.0", ContentPackFormat.CurrentFormatVersion),
            [new Track("track.dsa.test", "DSA test", [lesson.Id])], [lesson]);
        return stream.ToArray();
    }
}
