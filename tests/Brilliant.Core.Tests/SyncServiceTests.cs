using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Brilliant.Core.Progress;
using Brilliant.Core.Sync;

namespace Brilliant.Core.Tests;

/// <summary>The sync loop on one device, against a fake server that keeps the protocol in memory.</summary>
public class SyncServiceTests
{
    private static readonly DeviceCredentials Creds = new(new Uri("http://nuc.local:8080/"), "dev-1", "tok-1");

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e)
        {
            if (Items.Any(x => x.Id == e.Id)) return false;
            Items.Add(e);
            return true;
        }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private sealed class MemoryCredentials(DeviceCredentials? held) : IDeviceCredentialStore
    {
        public DeviceCredentials? Held { get; set; } = held;
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Held);
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) { Held = credentials; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct = default) { Held = null; return Task.CompletedTask; }
    }

    private sealed class MemoryState : ISyncStateStore
    {
        public SyncState State { get; set; } = SyncState.None;
        public SyncState Load() => State;
        public void Save(SyncState state) => State = state;
    }

    /// <summary>The server's two endpoints, in memory.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public List<ProgressEvent> Events { get; } = [];
        public int PageSize { get; set; } = 1000;
        public bool Down { get; set; }
        public HttpStatusCode? Status { get; set; }
        public int Pushes { get; private set; }
        public int Pulls { get; set; }
        public List<int> PushSizes { get; } = [];
        public string? LastAuth { get; private set; }

        /// <summary>Makes the pull answer with a cursor that never moves.</summary>
        public bool StuckCursor { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down) throw new HttpRequestException("No route to host");
            LastAuth = request.Headers.Authorization?.ToString();
            if (Status is { } status) return new HttpResponseMessage(status);

            if (request.Method == HttpMethod.Post)
            {
                Pushes++;
                var body = (await request.Content!.ReadFromJsonAsync<PushEventsRequest>(ct))!;
                PushSizes.Add(body.Events.Count);
                var fresh = body.Events.Where(e => Events.All(x => x.Id != e.Id)).DistinctBy(e => e.Id).ToList();
                Events.AddRange(fresh);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PushEventsResponse(fresh.Count, body.Events.Count - fresh.Count)) };
            }

            Pulls++;
            var after = int.Parse(request.RequestUri!.Query.Split('=')[1]);
            var page = Events.Skip(after).Take(PageSize).ToList();
            var cursor = StuckCursor ? after : after + page.Count;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PullEventsResponse(page, cursor, after + page.Count < Events.Count)) };
        }
    }

    private readonly MemoryLog _log = new();
    private readonly FakeServer _server = new();
    private readonly MemoryCredentials _creds = new(Creds);
    private readonly MemoryState _state = new();
    private readonly Clock _clock = new();
    private int _n;

    private SyncService Sync => new(_log, "me", _creds, new SyncClient(new HttpClient(_server)), _state, _clock);

    private ProgressEvent Mine() => Event("me");

    private ProgressEvent Event(string device) =>
        new($"e{++_n}", device, _clock.Now.AddMinutes(_n), ProgressEventTypes.StepCompleted, "lesson.1", $"step.{_n}", null);

    [Fact]
    public async Task A_first_sync_sends_this_devices_events_and_brings_in_the_servers()
    {
        var theirs = Event("other");
        _server.Events.Add(theirs);
        var mine = Mine();
        _log.Append(mine);

        var result = await Sync.SyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal((1, 1), (result.Pushed, result.Pulled));
        Assert.Contains(mine, _server.Events);
        Assert.Contains(theirs, _log.Items);
        Assert.Equal(_clock.Now, result.At);
        Assert.Equal("Bearer tok-1", _server.LastAuth);
    }

    [Fact]
    public async Task Syncing_again_with_nothing_new_sends_and_receives_nothing()
    {
        _log.Append(Mine());
        var sync = Sync;
        await sync.SyncAsync();
        _server.PushSizes.Clear();

        var result = await sync.SyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal((0, 0), (result.Pushed, result.Pulled));
        Assert.Empty(_server.PushSizes);   // nothing own and new: no request at all
        Assert.Single(_server.Events);
    }

    [Fact]
    public async Task Only_this_devices_own_events_are_pushed_never_ones_that_came_from_elsewhere()
    {
        _server.Events.Add(Event("other"));
        await Sync.SyncAsync();   // pulls the other device's event into the local log

        var mine = Mine();
        _log.Append(mine);
        _server.PushSizes.Clear();
        await Sync.SyncAsync();

        Assert.Equal([1], _server.PushSizes);
        Assert.Equal(2, _server.Events.Count);
        Assert.Contains(mine, _server.Events);
    }

    [Fact]
    public async Task A_new_event_after_a_sync_goes_up_on_the_next_one_and_only_it()
    {
        _log.Append(Mine());
        await Sync.SyncAsync();
        _server.PushSizes.Clear();
        _log.Append(Mine());

        var result = await Sync.SyncAsync();

        Assert.Equal(1, result.Pushed);
        Assert.Equal([1], _server.PushSizes);
    }

    [Fact]
    public async Task A_big_backlog_goes_up_in_batches_the_server_accepts()
    {
        for (var i = 0; i < EventLimits.MaxBatch * 2 + 10; i++) _log.Append(Mine());

        var result = await Sync.SyncAsync();

        Assert.Equal(EventLimits.MaxBatch * 2 + 10, result.Pushed);
        Assert.Equal([EventLimits.MaxBatch, EventLimits.MaxBatch, 10], _server.PushSizes);
    }

    [Fact]
    public async Task The_servers_pages_are_followed_until_there_are_no_more()
    {
        _server.PageSize = 3;
        for (var i = 0; i < 8; i++) _server.Events.Add(Event("other"));

        var result = await Sync.SyncAsync();

        Assert.Equal(8, result.Pulled);
        Assert.Equal(3, _server.Pulls);
        Assert.Equal(_server.Events.Select(e => e.Id), _log.Items.Select(e => e.Id));
        Assert.Equal(8, _state.State.PulledThrough);
    }

    [Fact]
    public async Task A_server_that_stops_moving_its_cursor_cannot_trap_the_loop()
    {
        _server.StuckCursor = true;
        _server.Events.Add(Event("other"));

        var result = await Sync.SyncAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded);
        Assert.Equal(1, _server.Pulls);
    }

    [Fact]
    public async Task A_sync_that_dies_part_way_resumes_where_it_stopped()
    {
        _server.PageSize = 2;
        for (var i = 0; i < 6; i++) _server.Events.Add(Event("other"));
        var client = new SyncClient(new HttpClient(new Interrupt(_server, afterPulls: 2)));
        var sync = new SyncService(_log, "me", _creds, client, _state, _clock);

        var first = await sync.SyncAsync();

        Assert.Equal(SyncFailure.Unreachable, first.Failure);
        Assert.Equal(4, _log.Items.Count);
        Assert.Equal(4, _state.State.PulledThrough);

        _server.Pulls = 0;
        var second = await Sync.SyncAsync();

        Assert.True(second.Succeeded);
        Assert.Equal(2, second.Pulled);   // not the four it already has
        Assert.Equal(1, _server.Pulls);
    }

    /// <summary>Lets some pulls through, then goes away.</summary>
    private sealed class Interrupt(FakeServer inner, int afterPulls) : DelegatingHandler(inner)
    {
        private int _seen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.Method == HttpMethod.Get && ++_seen > afterPulls
                ? throw new HttpRequestException("The connection dropped")
                : base.SendAsync(request, ct);
    }

    [Fact]
    public async Task Events_already_in_the_log_are_not_counted_or_doubled_when_pulled_again()
    {
        var shared = Event("other");
        _server.Events.Add(shared);
        _log.Append(shared);   // arrived some other way

        var result = await Sync.SyncAsync();

        Assert.Equal(0, result.Pulled);
        Assert.Single(_log.Items);
    }

    [Fact]
    public async Task When_the_server_is_away_the_log_is_untouched_and_the_result_says_so()
    {
        var mine = Mine();
        _log.Append(mine);
        _server.Down = true;

        var result = await Sync.SyncAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SyncFailure.Unreachable, result.Failure);
        Assert.Equal([mine], _log.Items);
        Assert.Null(_state.State.LastSyncedAt);
    }

    [Fact]
    public async Task Events_not_pushed_while_away_go_up_once_the_server_is_back()
    {
        var mine = Mine();
        _log.Append(mine);
        _server.Down = true;
        await Sync.SyncAsync();
        _server.Down = false;

        var result = await Sync.SyncAsync();

        Assert.True(result.Succeeded);
        Assert.Contains(mine, _server.Events);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SyncFailure.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, SyncFailure.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, SyncFailure.ServerError)]
    public async Task A_refusal_or_error_from_the_server_is_a_result_not_an_exception(HttpStatusCode status, SyncFailure expected)
    {
        _log.Append(Mine());
        _server.Status = status;

        var result = await Sync.SyncAsync();

        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task A_device_that_is_not_paired_does_nothing_and_says_so()
    {
        _creds.Held = null;

        var result = await Sync.SyncAsync();

        Assert.Equal(SyncFailure.NotPaired, result.Failure);
        Assert.Equal(0, _server.Pushes + _server.Pulls);
    }

    [Fact]
    public async Task A_log_that_cannot_be_read_is_a_local_error_not_a_crash()
    {
        var sync = new SyncService(new BrokenLog(), "me", _creds, new SyncClient(new HttpClient(_server)), _state, _clock);

        var result = await sync.SyncAsync();

        Assert.Equal(SyncFailure.LocalError, result.Failure);
        Assert.Contains("disk is full", result.Detail);
    }

    private sealed class BrokenLog : IProgressEventLog
    {
        public bool Append(ProgressEvent evt) => throw new IOException("disk is full");
        public IReadOnlyList<ProgressEvent> ReadAll() => throw new IOException("disk is full");
        public EventPage ReadSince(long cursor) => throw new IOException("disk is full");
    }

    [Fact]
    public async Task A_new_pairing_starts_over_and_the_idempotent_exchange_makes_that_harmless()
    {
        var mine = Mine();
        _log.Append(mine);
        await Sync.SyncAsync();
        Assert.Equal("dev-1", _state.State.PairingId);

        // Paired again, with a server that has its own copy of the history.
        _creds.Held = Creds with { DeviceId = "dev-2", Token = "tok-2" };
        _server.PushSizes.Clear();
        var result = await Sync.SyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("dev-2", _state.State.PairingId);
        Assert.Equal([1], _server.PushSizes);   // offered again from the start...
        Assert.Equal(0, result.Pushed);         // ...and the server already had it
        Assert.Single(_server.Events);
    }

    [Fact]
    public async Task The_last_sync_time_is_kept_per_pairing()
    {
        var sync = Sync;
        Assert.Null(sync.LastSyncedAt("dev-1"));

        await sync.SyncAsync();

        Assert.Equal(_clock.Now, sync.LastSyncedAt("dev-1"));
        Assert.Null(sync.LastSyncedAt("dev-2"));
    }

    [Fact]
    public async Task A_failed_sync_leaves_the_last_successful_time_alone()
    {
        var sync = Sync;
        await sync.SyncAsync();
        var synced = _clock.Now;
        _clock.Now = _clock.Now.AddHours(1);
        _server.Down = true;

        await sync.SyncAsync();

        Assert.Equal(synced, sync.LastSyncedAt("dev-1"));
        Assert.Equal(SyncFailure.Unreachable, sync.Last!.Failure);
    }

    [Fact]
    public async Task Pulled_events_are_announced_so_screens_can_redraw_and_a_quiet_sync_is_not()
    {
        var sync = Sync;
        var announced = 0;
        sync.EventsPulled += () => announced++;

        await sync.SyncAsync();
        Assert.Equal(0, announced);

        _server.Events.Add(Event("other"));
        await sync.SyncAsync();
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task Status_changes_are_announced_when_a_sync_starts_and_finishes()
    {
        var sync = Sync;
        var seen = new List<bool>();
        sync.StatusChanged += () => seen.Add(sync.IsSyncing);

        await sync.SyncAsync();

        Assert.Equal([true, false], seen);
        Assert.False(sync.IsSyncing);
    }

    [Fact]
    public async Task Two_syncs_at_once_take_turns()
    {
        var gate = new TaskCompletionSource();
        var slow = new Gate(_server, gate.Task);
        var sync = new SyncService(_log, "me", _creds, new SyncClient(new HttpClient(slow)), _state, _clock);

        var first = sync.SyncAsync();
        var second = sync.SyncAsync();
        await Task.Delay(50);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, slow.MaxConcurrent);

        gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, slow.MaxConcurrent);
    }

    private sealed class Gate(FakeServer inner, Task open) : DelegatingHandler(inner)
    {
        private int _now;
        public int MaxConcurrent { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _now));
            try
            {
                await open;
                return await base.SendAsync(request, ct);
            }
            finally { Interlocked.Decrement(ref _now); }
        }
    }

    [Fact]
    public async Task Cancelling_a_sync_is_not_reported_as_a_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sync.SyncAsync(cts.Token));
    }

    [Fact]
    public async Task A_corrupt_state_file_just_means_starting_over()
    {
        var dir = Path.Combine(Path.GetTempPath(), "brilliant-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "sync-state.json");
            await File.WriteAllTextAsync(path, "{ nope");
            var store = new FileSyncStateStore(path);
            Assert.Equal(SyncState.None, store.Load());

            var state = new SyncState("dev-1", 7, 9, new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero));
            store.Save(state);

            Assert.Equal(state, new FileSyncStateStore(path).Load());
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task The_scheduler_syncs_at_start_and_then_on_every_tick_and_survives_a_dead_server()
    {
        _server.Down = true;
        var sync = Sync;
        var attempts = 0;
        sync.StatusChanged += () => { if (sync.IsSyncing) Interlocked.Increment(ref attempts); };
        using var scheduler = new SyncScheduler(sync, TimeSpan.FromMilliseconds(30));

        scheduler.Start();
        scheduler.Start();   // starting twice doesn't double up

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref attempts) < 3 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(Volatile.Read(ref attempts) >= 3);
        Assert.Equal(SyncFailure.Unreachable, sync.Last!.Failure);
    }
}
