using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Sync;

namespace Brilliant.Core.Tests;

internal static class Packs
{
    public static ExplainStep Explain(string id) => new(id, id, "body", [], null);

    public static byte[] Build(string version, string packId = "pack.t", params string[] stepIds)
    {
        if (stepIds.Length == 0) stepIds = ["step.a", "step.b"];
        var lesson = new Lesson("lesson.1", "One", "track.a", stepIds.Select(Explain).ToList<Step>());
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest(packId, version, ContentPackFormat.CurrentFormatVersion), [new Track("track.a", "A", [lesson.Id])], [lesson]);
        return ms.ToArray();
    }

    public static ContentGraph Graph(byte[] pack) => ContentPackFormat.Load(new MemoryStream(pack));

    public static PackInfo Info(byte[] pack, string? version = null, string? packId = null, int format = ContentPackFormat.CurrentFormatVersion) =>
        new(packId ?? Graph(pack).Manifest.PackId, version ?? Graph(pack).Manifest.Version, format, Convert.ToHexStringLower(SHA256.HashData(pack)), pack.Length, DateTimeOffset.UnixEpoch);
}

public class PackVersionTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("12.345.6789", 12, 345, 6789)]
    [InlineData("007.0.0", 7, 0, 0)]
    public void Three_numbers_are_a_version(string text, int major, int minor, int patch)
    {
        Assert.True(PackVersion.TryParse(text, out var v));
        Assert.Equal(new PackVersion(major, minor, patch), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.x")]
    [InlineData("1.0.-1")]
    [InlineData("1.0.0-beta")]
    [InlineData("1..0")]
    [InlineData("v1.0.0")]
    [InlineData(" 1.0.0")]
    [InlineData("99999999999.0.0")]
    public void Anything_else_is_not(string text) => Assert.False(PackVersion.TryParse(text, out _));

    [Fact]
    public void Versions_compare_by_number_not_by_text()
    {
        Assert.True(PackVersion.Parse("0.10.0") > PackVersion.Parse("0.9.0"));
        Assert.True(PackVersion.Parse("1.0.0") > PackVersion.Parse("0.99.99"));
        Assert.True(PackVersion.Parse("1.2.10") > PackVersion.Parse("1.2.9"));
        Assert.Equal("1.2.3", PackVersion.Parse("01.2.3").ToString());
    }

    [Theory]
    [InlineData("pack.track-1", true)]
    [InlineData("a_b-C.1", true)]
    [InlineData("", false)]
    [InlineData(".hidden", false)]
    [InlineData("../evil", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("a\\b", false)]
    public void Pack_ids_are_safe_to_use_in_a_file_name_and_a_url(string id, bool ok) => Assert.Equal(ok, PackIds.IsValid(id));

    [Fact]
    public void A_pack_id_is_at_most_a_hundred_characters()
    {
        Assert.True(PackIds.IsValid(new string('a', 100)));
        Assert.False(PackIds.IsValid(new string('a', 101)));
    }
}

public sealed class PackCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "brilliant-packcache-" + Guid.NewGuid().ToString("N"));
    private PackCache Cache => new(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void A_stored_pack_is_found_when_it_is_newer_than_what_runs()
    {
        Cache.Store("pack.t", PackVersion.Parse("1.1.0"), Packs.Build("1.1.0"));

        var (graph, version) = Cache.LoadNewest("pack.t", PackVersion.Parse("1.0.0"))!.Value;

        Assert.Equal(PackVersion.Parse("1.1.0"), version);
        Assert.Equal("1.1.0", graph.Manifest.Version);
    }

    [Fact]
    public void A_pack_that_is_not_newer_is_not_chosen()
    {
        Cache.Store("pack.t", PackVersion.Parse("1.0.0"), Packs.Build("1.0.0"));

        Assert.Null(Cache.LoadNewest("pack.t", PackVersion.Parse("1.0.0")));
    }

    [Fact]
    public void A_pack_of_another_id_is_never_chosen()
    {
        Cache.Store("pack.other", PackVersion.Parse("9.0.0"), Packs.Build("9.0.0", "pack.other"));

        Assert.Null(Cache.LoadNewest("pack.t", PackVersion.Parse("1.0.0")));
    }

    [Fact]
    public void A_pack_that_is_not_what_it_says_is_refused_and_not_stored()
    {
        Assert.Throws<ContentPackException>(() => Cache.Store("pack.t", PackVersion.Parse("2.0.0"), Packs.Build("1.5.0")));
        Assert.Throws<ContentPackException>(() => Cache.Store("pack.t", PackVersion.Parse("1.0.0"), Packs.Build("1.0.0", "pack.other")));
        Assert.Throws<ContentPackException>(() => Cache.Store("pack.t", PackVersion.Parse("1.0.0"), [1, 2, 3]));

        Assert.Null(Cache.Newest("pack.t"));
    }

    [Fact]
    public void A_damaged_newest_file_is_dropped_and_the_next_newest_is_used()
    {
        Cache.Store("pack.t", PackVersion.Parse("1.1.0"), Packs.Build("1.1.0"));
        File.WriteAllBytes(Path.Combine(_dir, "pack.t_1.2.0.zip"), [0x50, 0x4b, 0, 0]);

        var (_, version) = Cache.LoadNewest("pack.t", PackVersion.Parse("1.0.0"))!.Value;

        Assert.Equal(PackVersion.Parse("1.1.0"), version);
        Assert.False(File.Exists(Path.Combine(_dir, "pack.t_1.2.0.zip")));
    }

    [Fact]
    public void A_file_whose_name_lies_about_its_version_is_dropped()
    {
        File.WriteAllBytes(Directory.CreateDirectory(_dir).FullName + "/pack.t_3.0.0.zip", Packs.Build("1.5.0"));

        Assert.Null(Cache.LoadNewest("pack.t", PackVersion.Parse("1.0.0")));
        Assert.False(File.Exists(Path.Combine(_dir, "pack.t_3.0.0.zip")));
    }

    [Fact]
    public void Only_the_newest_few_versions_are_kept()
    {
        foreach (var v in new[] { "1.1.0", "1.2.0", "1.3.0", "1.4.0" }) Cache.Store("pack.t", PackVersion.Parse(v), Packs.Build(v));

        Assert.Equal(["pack.t_1.3.0.zip", "pack.t_1.4.0.zip"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Versions_are_compared_as_numbers_when_choosing_and_pruning()
    {
        foreach (var v in new[] { "1.9.0", "1.10.0", "1.2.0" }) Cache.Store("pack.t", PackVersion.Parse(v), Packs.Build(v));

        Assert.Equal(PackVersion.Parse("1.10.0"), Cache.Newest("pack.t"));
        Assert.False(File.Exists(Path.Combine(_dir, "pack.t_1.2.0.zip")));
    }

    [Fact]
    public void A_missing_folder_is_just_empty()
    {
        Assert.Null(Cache.Newest("pack.t"));
        Assert.Null(Cache.LoadNewest("pack.t", default));
    }

    [Fact]
    public void Startup_uses_the_bundled_pack_when_nothing_newer_is_cached()
    {
        var graph = PackStartup.Choose(() => new MemoryStream(Packs.Build("1.0.0")), Cache);

        Assert.Equal("1.0.0", graph.Manifest.Version);
    }

    [Fact]
    public void Startup_uses_a_newer_cached_pack()
    {
        Cache.Store("pack.t", PackVersion.Parse("1.1.0"), Packs.Build("1.1.0", stepIds: ["step.a", "step.b", "step.c"]));

        var graph = PackStartup.Choose(() => new MemoryStream(Packs.Build("1.0.0")), Cache);

        Assert.Equal("1.1.0", graph.Manifest.Version);
        Assert.True(graph.TryGet<Step>("step.c", out _));
    }

    [Fact]
    public void Startup_never_goes_back_to_an_older_cached_pack_than_the_bundled_one()
    {
        Cache.Store("pack.t", PackVersion.Parse("0.9.0"), Packs.Build("0.9.0"));

        Assert.Equal("1.0.0", PackStartup.Choose(() => new MemoryStream(Packs.Build("1.0.0")), Cache).Manifest.Version);
    }

    [Fact]
    public void Startup_survives_a_cache_that_cannot_be_read()
    {
        File.WriteAllText(_dir, "a file where the folder should be");
        try
        {
            var graph = PackStartup.Choose(() => new MemoryStream(Packs.Build("1.0.0")), new PackCache(_dir));
            Assert.Equal("1.0.0", graph.Manifest.Version);
        }
        finally { File.Delete(_dir); }
    }
}

public sealed class ContentUpdateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "brilliant-update-" + Guid.NewGuid().ToString("N"));
    private static readonly DeviceCredentials Paired = new(new Uri("http://server:8080/"), "dev", "tok");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private sealed class MemoryStore(DeviceCredentials? creds) : IDeviceCredentialStore
    {
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult(creds);
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A server holding some packs. Counts the downloads.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public List<byte[]> Held { get; } = [];
        public List<PackInfo>? Listing;   // overrides what GET /packs says
        public HttpStatusCode? Fail;
        public int Downloads;
        public Func<byte[], byte[]> Corrupt = b => b;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fail is { } status) return Task.FromResult(new HttpResponseMessage(status));
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/packs")
            {
                var infos = Listing ?? Held.Select(b => Packs.Info(b)).GroupBy(i => i.PackId).Select(g => g.OrderByDescending(i => PackVersion.Parse(i.Version)).First()).ToList();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PackListResponse(infos)) });
            }
            Downloads++;
            var wanted = path.Split('/')[^1];
            var bytes = Held.FirstOrDefault(b => Packs.Graph(b).Manifest.Version == wanted);
            return Task.FromResult(bytes is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Corrupt(bytes)) });
        }
    }

    private (ContentUpdateService Service, FakeServer Server, PackCache Cache) Start(string runningVersion = "1.0.0", bool paired = true)
    {
        var server = new FakeServer();
        var cache = new PackCache(_dir);
        var running = Packs.Graph(Packs.Build(runningVersion));
        return (new ContentUpdateService(running, cache, new PackClient(new HttpClient(server)), new MemoryStore(paired ? Paired : null)), server, cache);
    }

    [Fact]
    public async Task A_newer_version_is_downloaded_and_cached_for_the_next_start()
    {
        var (service, server, cache) = Start();
        server.Held.Add(Packs.Build("1.0.0"));
        server.Held.Add(Packs.Build("1.1.0"));

        var result = await service.CheckAsync();

        Assert.Equal((ContentUpdateOutcome.Downloaded, "1.1.0"), (result.Outcome, result.Version));
        Assert.Equal("1.1.0", service.PendingVersion);
        Assert.Equal(PackVersion.Parse("1.1.0"), cache.Newest("pack.t"));
    }

    [Fact]
    public async Task The_same_version_is_up_to_date_and_downloads_nothing()
    {
        var (service, server, _) = Start();
        server.Held.Add(Packs.Build("1.0.0"));

        var result = await service.CheckAsync();

        Assert.Equal(ContentUpdateOutcome.UpToDate, result.Outcome);
        Assert.Equal(0, server.Downloads);
        Assert.Null(service.PendingVersion);
    }

    [Fact]
    public async Task A_version_already_downloaded_is_not_downloaded_again()
    {
        var (service, server, _) = Start();
        server.Held.Add(Packs.Build("1.1.0"));

        await service.CheckAsync();
        var second = await service.CheckAsync();

        Assert.Equal(1, server.Downloads);
        Assert.Equal((ContentUpdateOutcome.UpToDate, "1.1.0"), (second.Outcome, second.Version));
    }

    [Fact]
    public async Task A_version_that_is_older_than_the_running_one_is_ignored()
    {
        var (service, server, _) = Start("1.2.0");
        server.Held.Add(Packs.Build("1.1.0"));

        Assert.Equal(ContentUpdateOutcome.UpToDate, (await service.CheckAsync()).Outcome);
        Assert.Equal(0, server.Downloads);
    }

    [Fact]
    public async Task Other_packs_on_the_server_are_not_this_apps_business()
    {
        var (service, server, _) = Start();
        server.Held.Add(Packs.Build("5.0.0", "pack.other"));

        Assert.Equal(ContentUpdateOutcome.UpToDate, (await service.CheckAsync()).Outcome);
        Assert.Equal(0, server.Downloads);
    }

    [Fact]
    public async Task A_pack_in_a_format_this_app_cannot_read_waits_for_a_newer_app()
    {
        var (service, server, _) = Start();
        var future = Packs.Build("2.0.0");
        server.Listing = [Packs.Info(future, format: ContentPackFormat.CurrentFormatVersion + 1)];
        server.Held.Add(future);

        Assert.Equal(ContentUpdateOutcome.UpToDate, (await service.CheckAsync()).Outcome);
        Assert.Equal(0, server.Downloads);
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_checksum_is_not_kept()
    {
        var (service, server, cache) = Start();
        server.Held.Add(Packs.Build("1.1.0"));
        server.Corrupt = b => [.. b, 0];

        var result = await service.CheckAsync();

        Assert.Equal(ContentUpdateOutcome.Failed, result.Outcome);
        Assert.Null(cache.Newest("pack.t"));
        Assert.Null(service.PendingVersion);
    }

    [Fact]
    public async Task A_pack_that_is_not_what_the_server_listed_is_refused_and_not_fetched_every_time()
    {
        var cache = new PackCache(_dir);
        var real = Packs.Build("1.1.0");
        var liar = new LyingServer(real, Packs.Info(real, version: "1.5.0"));   // listed as 1.5.0, but the file inside is 1.1.0
        var service = new ContentUpdateService(Packs.Graph(Packs.Build("1.0.0")), cache, new PackClient(new HttpClient(liar)), new MemoryStore(Paired));

        var first = await service.CheckAsync();
        var second = await service.CheckAsync();

        Assert.Equal(ContentUpdateOutcome.Failed, first.Outcome);
        Assert.Equal(ContentUpdateOutcome.UpToDate, second.Outcome);
        Assert.Equal(1, liar.Downloads);
        Assert.Null(cache.Newest("pack.t"));
    }

    private sealed class LyingServer(byte[] bytes, PackInfo listed) : HttpMessageHandler
    {
        public int Downloads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/packs")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PackListResponse([listed])) });
            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    [Fact]
    public async Task An_unpaired_device_asks_nobody()
    {
        var (service, server, _) = Start(paired: false);
        server.Held.Add(Packs.Build("1.1.0"));

        Assert.Equal(ContentUpdateOutcome.NotPaired, (await service.CheckAsync()).Outcome);
        Assert.Equal(0, server.Downloads);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SyncFailure.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, SyncFailure.ServerError)]
    [InlineData(HttpStatusCode.NotFound, SyncFailure.ServerError)]   // an older server with no /packs
    public async Task A_server_that_refuses_is_reported_and_nothing_throws(HttpStatusCode status, SyncFailure failure)
    {
        var (service, server, _) = Start();
        server.Fail = status;

        var result = await service.CheckAsync();

        Assert.Equal((ContentUpdateOutcome.Failed, failure), (result.Outcome, result.Failure));
    }

    [Fact]
    public async Task A_server_that_is_away_is_just_unreachable()
    {
        var service = new ContentUpdateService(Packs.Graph(Packs.Build("1.0.0")), new PackCache(_dir),
            new PackClient(new HttpClient(new Down())), new MemoryStore(Paired));

        var result = await service.CheckAsync();

        Assert.Equal((ContentUpdateOutcome.Failed, SyncFailure.Unreachable), (result.Outcome, result.Failure));
    }

    private sealed class Down : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("Connection refused");
    }

    [Fact]
    public async Task A_check_tells_listeners_it_finished()
    {
        var (service, _, _) = Start();
        var raised = 0;
        service.Changed += () => raised++;

        await service.CheckAsync();

        Assert.Equal(1, raised);
    }
}

/// <summary>"Progress is preserved across content updates (stable IDs)": the learner's log is projected against whichever pack runs.</summary>
public class ProgressAcrossContentUpdateTests
{
    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static ContentGraph Pack(string version, params string[] stepIds) => Packs.Graph(Packs.Build(version, "pack.t", stepIds));

    private static LessonState LessonOf(ProgressRecorder recorder, ContentGraph content) =>
        recorder.Project(content).Tracks.SelectMany(t => t.Lessons).Single(l => l.Lesson.Id == "lesson.1");

    [Fact]
    public void Steps_done_before_an_update_are_still_done_after_it()
    {
        var recorder = new ProgressRecorder(new MemoryLog(), "dev");
        var v1 = Pack("1.0.0", "step.a", "step.b");
        recorder.CompleteStep(v1.Get<Lesson>("lesson.1"), "step.a");

        var v2 = Pack("1.1.0", "step.a", "step.b", "step.c");   // a step is added
        var after = LessonOf(recorder, v2);

        Assert.Equal(LessonStatus.InProgress, after.Status);
        Assert.Contains("step.a", after.Progress.CompletedStepIds);
        Assert.Equal(1, after.Progress.CompletedCount);
        Assert.Equal(3, after.Progress.TotalSteps);
    }

    [Fact]
    public void A_finished_lesson_stays_finished_when_a_step_is_added()
    {
        var recorder = new ProgressRecorder(new MemoryLog(), "dev");
        var v1 = Pack("1.0.0", "step.a", "step.b");
        foreach (var id in new[] { "step.a", "step.b" }) recorder.CompleteStep(v1.Get<Lesson>("lesson.1"), id);
        Assert.Equal(LessonStatus.Completed, LessonOf(recorder, v1).Status);

        var v2 = Pack("1.1.0", "step.a", "step.b", "step.c");
        var after = LessonOf(recorder, v2);

        Assert.Equal(LessonStatus.Completed, after.Status);
        Assert.Equal(1, after.NewStepCount);
    }

    [Fact]
    public void Progress_on_a_step_an_update_removed_is_kept_and_does_not_break_anything()
    {
        var log = new MemoryLog();
        var recorder = new ProgressRecorder(log, "dev");
        var v1 = Pack("1.0.0", "step.a", "step.b");
        recorder.CompleteStep(v1.Get<Lesson>("lesson.1"), "step.a");
        recorder.CompleteStep(v1.Get<Lesson>("lesson.1"), "step.b");
        var eventsBefore = log.Items.Count;

        var v2 = Pack("1.1.0", "step.a");   // step.b is gone
        var after = LessonOf(recorder, v2);

        Assert.Equal(LessonStatus.Completed, after.Status);
        Assert.Equal(eventsBefore, log.Items.Count);   // the log itself is untouched
        // and coming back to the old content shows the same progress
        Assert.Equal(LessonStatus.Completed, LessonOf(recorder, v1).Status);
    }

    [Fact]
    public void Deleting_the_last_unfinished_step_completes_the_lesson_once_and_for_all()
    {
        var recorder = new ProgressRecorder(new MemoryLog(), "dev");
        var v1 = Pack("1.0.0", "step.a", "step.b");
        recorder.CompleteStep(v1.Get<Lesson>("lesson.1"), "step.a");

        var v2 = Pack("1.1.0", "step.a");   // the unfinished step.b is deleted
        Assert.Equal(1, recorder.RecordCompletionsFrom(v2));
        Assert.Equal(0, recorder.RecordCompletionsFrom(v2));

        var v3 = Pack("1.2.0", "step.a", "step.x");   // a later update adds a new step
        Assert.Equal(LessonStatus.Completed, LessonOf(recorder, v3).Status);
    }

    [Fact]
    public void Progress_on_an_edited_step_carries_over_because_the_id_did_not_change()
    {
        var recorder = new ProgressRecorder(new MemoryLog(), "dev");
        var v1 = Pack("1.0.0", "step.a", "step.b");
        recorder.CompleteStep(v1.Get<Lesson>("lesson.1"), "step.a");

        var lesson = new Lesson("lesson.1", "One, reworded", "track.a", [new ExplainStep("step.a", "Reworded title", "new body", [], null), Packs.Explain("step.b")]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "1.0.1", 1), [new Track("track.a", "A", [lesson.Id])], [lesson]);
        var v2 = Packs.Graph(ms.ToArray());

        Assert.Contains("step.a", LessonOf(recorder, v2).Progress.CompletedStepIds);
    }
}
