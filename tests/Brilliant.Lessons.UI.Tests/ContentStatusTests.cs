using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Sync;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The Sync screen's line about content: which version runs, and whether a newer one is waiting for a restart.</summary>
public sealed class ContentStatusTests : BunitContext, IDisposable
{
    private static readonly DeviceCredentials Paired = new(new Uri("http://nuc.local:8080/"), "dev-1", "tok-1");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "brilliant-ui-packs-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new();

    private sealed class Store(DeviceCredentials? creds) : IDeviceCredentialStore
    {
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult(creds);
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyLog : IProgressEventLog
    {
        public bool Append(ProgressEvent evt) => true;
        public IReadOnlyList<ProgressEvent> ReadAll() => [];
        public EventPage ReadSince(long cursor) => new([], 0);
    }

    private sealed class MemorySyncState : ISyncStateStore
    {
        public SyncState State = SyncState.None;
        public SyncState Load() => State;
        public void Save(SyncState state) => State = state;
    }

    /// <summary>The sync endpoints (empty) and the packs endpoints (whatever <see cref="Offered"/> is).</summary>
    private sealed class Server : HttpMessageHandler
    {
        public byte[]? Offered;
        public bool Down;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down) throw new HttpRequestException("No route to host");
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/packs"))
            {
                if (Offered is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PackListResponse([])) });
                var graph = ContentPackFormat.Load(new MemoryStream(Offered));
                var info = new PackInfo(graph.Manifest.PackId, graph.Manifest.Version, graph.Manifest.FormatVersion, Convert.ToHexStringLower(SHA256.HashData(Offered)), Offered.Length, DateTimeOffset.UnixEpoch);
                return Task.FromResult(path == "/packs"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PackListResponse([info])) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Offered) });
            }
            return Task.FromResult(request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PushEventsResponse(0, 0)) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PullEventsResponse([], 0, false)) });
        }
    }

    private static byte[] BuildPack(string version)
    {
        var lesson = new Lesson("lesson.1", "One", "track.a", [new ExplainStep("step.a", "A", "b", [], null)]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", version, ContentPackFormat.CurrentFormatVersion), [new Track("track.a", "A", [lesson.Id])], [lesson]);
        return ms.ToArray();
    }

    private readonly Server _server = new();

    private IRenderedComponent<PairingView> Open(bool withUpdates = true, bool paired = true)
    {
        var store = new Store(paired ? Paired : null);
        Services.AddSingleton(new PairingService(new PairingClient(new HttpClient(_server)), store));
        Services.AddSingleton(new SyncService(new EmptyLog(), "me", store, new SyncClient(new HttpClient(_server)), new MemorySyncState(), _clock));
        Services.AddSingleton<TimeProvider>(_clock);
        if (withUpdates)
        {
            var running = ContentPackFormat.Load(new MemoryStream(BuildPack("1.0.0")));
            Services.AddSingleton(new ContentUpdateService(running, new PackCache(_dir), new PackClient(new HttpClient(_server)), store, _clock));
        }
        return Render<PairingView>();
    }

    public new void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_paired_device_says_which_content_it_runs()
    {
        var cut = Open();

        Assert.Contains("Content 1.0.0", cut.Find(".content-status").TextContent);
    }

    [Fact]
    public void Sync_now_with_nothing_newer_says_up_to_date()
    {
        var cut = Open();

        cut.Find("button.sync-now").Click();

        cut.WaitForAssertion(() => Assert.Contains("1.0.0 is up to date", cut.Find(".content-status").TextContent));
    }

    [Fact]
    public void Sync_now_downloads_a_newer_version_and_asks_for_a_restart()
    {
        _server.Offered = BuildPack("1.1.0");
        var cut = Open();

        cut.Find("button.sync-now").Click();

        cut.WaitForAssertion(() =>
        {
            var text = cut.Find(".content-status").TextContent;
            Assert.Contains("1.1.0 downloaded", text);
            Assert.Contains("Restart", text);
        });
        Assert.True(File.Exists(Path.Combine(_dir, "pack.t_1.1.0.zip")));
    }

    [Fact]
    public void A_server_that_is_away_does_not_hide_which_content_runs()
    {
        _server.Down = true;
        var cut = Open();

        cut.Find("button.sync-now").Click();

        cut.WaitForAssertion(() => Assert.Contains("Couldn't check for new content. You're on 1.0.0", cut.Find(".content-status").TextContent));
    }

    [Fact]
    public void Hosts_without_content_updates_show_no_content_line()
    {
        var cut = Open(withUpdates: false);

        Assert.Empty(cut.FindAll(".content-status"));
        cut.Find("button.sync-now");   // and Sync now still works
    }
}
