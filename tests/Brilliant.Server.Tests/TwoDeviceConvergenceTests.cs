using Brilliant.Core.Progress;
using Brilliant.Core.Sync;
using Brilliant.Core.Tests;
using Brilliant.Data;

namespace Brilliant.Server.Tests;

/// <summary>
/// The point of #29: two devices that each learned offline, syncing through the real server and their real SQLite logs, end up
/// holding the same events and showing the same learner state.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TwoDeviceConvergenceTests(PostgresFixture postgres) : IAsyncLifetime
{
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

    /// <summary>A device as the app has it: its own SQLite log, recorder and sync service, reaching the server through a switchable link.</summary>
    private sealed class Device : IDisposable
    {
        public SqliteProgressEventLog Log { get; } = new("Data Source=:memory:");
        public SqliteCodeDraftStore Drafts { get; }
        public ProgressRecorder Recorder { get; }
        public SyncService Sync { get; }
        public bool Online { get; set; } = true;

        public Device(string name, HttpMessageHandler server, string token, SyncFixtures.Clock clock)
        {
            Recorder = new ProgressRecorder(Log, name, clock);
            Drafts = new SqliteCodeDraftStore("Data Source=:memory:", () => clock.GetUtcNow());
            var link = new HttpClient(new Link(this, server));
            Sync = new SyncService(Log, name, new MemoryCredentials(new DeviceCredentials(new Uri("http://localhost/"), name, token)),
                new SyncClient(link), new MemoryState(), clock, Drafts);
        }

        public void Dispose() { Log.Dispose(); Drafts.Dispose(); }

        /// <summary>The network between the device and the server: passes requests through while the device is online, and fails as a missing route does when it is not.</summary>
        private sealed class Link(Device device, HttpMessageHandler server) : DelegatingHandler(server)
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                device.Online ? base.SendAsync(request, ct) : throw new HttpRequestException("No route to host");
        }
    }

    private ServerHarness _server = null!;
    private Device _a = null!, _b = null!;
    private readonly SyncFixtures.Clock _clock = new(SyncFixtures.T0);

    public async Task InitializeAsync()
    {
        _server = await ServerHarness.StartAsync(postgres);
        var (_, a) = await _server.PairDeviceAsync("Mac");
        var (_, b) = await _server.PairDeviceAsync("Phone");
        _a = new Device("device-a", _server.Server.CreateHandler(), a.Token, _clock);
        _b = new Device("device-b", _server.Server.CreateHandler(), b.Token, _clock);
    }

    public async Task DisposeAsync()
    {
        _a.Dispose();
        _b.Dispose();
        await _server.DisposeAsync();
    }

    private static string Print(Device d) => LearnerStateFingerprint.Of(d.Log.ReadAll(), SyncFixtures.Content, SyncFixtures.T0.AddDays(2));

    private static List<string> Ids(Device d) => d.Log.ReadAll().Select(e => e.Id).Order(StringComparer.Ordinal).ToList();

    private async Task SettleAsync()
    {
        // Each device pushes, then pulls: two rounds in turn leave both with everything.
        foreach (var d in new[] { _a, _b, _a })
        {
            var result = await d.Sync.SyncAsync();
            Assert.True(result.Succeeded, $"{result.Failure}: {result.Detail}");
        }
    }

    [Fact]
    public async Task Two_devices_that_learned_offline_converge_to_identical_events_and_learner_state()
    {
        SyncFixtures.TwoDevicesApart(_a.Recorder, _b.Recorder, _clock);
        Assert.NotEqual(Print(_a), Print(_b));   // apart, they disagree
        var ids = Ids(_a).Concat(Ids(_b)).Order(StringComparer.Ordinal).ToList();

        await SettleAsync();

        Assert.Equal(ids.Count, Ids(_a).Count);
        Assert.Equal(ids, Ids(_a));
        Assert.Equal(ids, Ids(_b));
        Assert.Equal(Print(_a), Print(_b));
        Assert.Contains("goal=12", Print(_a));   // the later goal won on both
    }

    [Fact]
    public async Task Events_come_back_exactly_as_recorded_on_the_device_that_pulled_them()
    {
        SyncFixtures.TwoDevicesApart(_a.Recorder, _b.Recorder, _clock);
        await SettleAsync();

        var mine = _a.Log.ReadAll().Where(e => e.DeviceId == "device-a").ToDictionary(e => e.Id);
        var viaServer = _b.Log.ReadAll().Where(e => e.DeviceId == "device-a").ToDictionary(e => e.Id);

        Assert.Equal(mine.Count, viaServer.Count);
        Assert.All(mine, kv => Assert.Equal(kv.Value, viaServer[kv.Key]));
    }

    [Fact]
    public async Task Syncing_again_changes_nothing_on_either_device_or_the_server()
    {
        SyncFixtures.TwoDevicesApart(_a.Recorder, _b.Recorder, _clock);
        await SettleAsync();
        var (idsBefore, printBefore) = (Ids(_a), Print(_a));
        var onServer = await _server.WithDbAsync(db => Task.FromResult(db.Events.Count()));

        foreach (var d in new[] { _a, _b, _a, _b })
        {
            var result = await d.Sync.SyncAsync();
            Assert.Equal((0, 0), (result.Pushed, result.Pulled));
        }

        Assert.Equal(idsBefore, Ids(_a));
        Assert.Equal(printBefore, Print(_a));
        Assert.Equal(onServer, await _server.WithDbAsync(db => Task.FromResult(db.Events.Count())));
    }

    [Fact]
    public async Task A_device_that_was_unreachable_catches_up_later_without_losing_or_doubling_anything()
    {
        SyncFixtures.TwoDevicesApart(_a.Recorder, _b.Recorder, _clock);
        _b.Online = false;
        await _a.Sync.SyncAsync();
        var failed = await _b.Sync.SyncAsync();
        Assert.Equal(SyncFailure.Unreachable, failed.Failure);
        Assert.NotEqual(Print(_a), Print(_b));

        // More learning on B while it is away, and on A meanwhile.
        _clock.Now = _clock.Now.AddMinutes(10);
        _b.Recorder.CompleteStep(SyncFixtures.Three, "step.three.intro");
        _a.Recorder.SetDailyGoal(20);

        _b.Online = true;
        await SettleAsync();

        Assert.Equal(Ids(_a), Ids(_b));
        Assert.Equal(Print(_a), Print(_b));
        Assert.Contains("goal=20", Print(_b));
    }

    [Fact]
    public async Task Events_recorded_between_syncs_flow_both_ways_and_stay_converged()
    {
        await SettleAsync();
        for (var round = 0; round < 3; round++)
        {
            _clock.Now = _clock.Now.AddMinutes(5);
            _a.Recorder.SetDailyGoal(5 + round);
            _b.Recorder.StepFlagged("lesson.one", "step.one.intro", $"round {round}");
            _a.Recorder.CompleteStep(SyncFixtures.One, round == 0 ? "step.one.intro" : "step.one.q" + round);
            await SettleAsync();

            Assert.Equal(Ids(_a), Ids(_b));
            Assert.Equal(Print(_a), Print(_b));
        }
        Assert.Equal(10, Ids(_a).Count);   // three rounds of a goal and a step from A and a flag from B, and the lesson completes in the last

    }

    [Fact]
    public async Task An_event_too_big_for_the_server_stays_on_its_device_and_blocks_nothing_for_anyone()
    {
        // An offline backlog on A: a long but acceptable submission, one far too big to share, and ordinary progress after both.
        _a.Recorder.CodeSubmitted("lesson.one", "step.one.q1", new string('a', 150_000), 3, 3);
        _clock.Now = _clock.Now.AddMinutes(1);
        _a.Recorder.CodeSubmitted("lesson.one", "step.one.q2", new string('b', EventLimits.MaxDataLength + 10), 3, 3);
        _clock.Now = _clock.Now.AddMinutes(1);
        _a.Recorder.SetDailyGoal(15);
        _b.Recorder.StepFlagged("lesson.one", "step.one.intro", "from the phone");
        var huge = _a.Log.ReadAll().Single(e => e.StepId == "step.one.q2");

        await SettleAsync();

        // A kept everything, including what the server could not take, and learned what B did.
        Assert.Contains(huge.Id, Ids(_a));
        Assert.Contains(_a.Log.ReadAll(), e => e.Type == ProgressEventTypes.StepFlagged);
        Assert.Equal(1, _a.Sync.RejectedCount("device-a"));

        // B has the rest, whole, and the server never held the big one.
        var onB = _b.Log.ReadAll();
        Assert.DoesNotContain(onB, e => e.Id == huge.Id);
        var longOne = _a.Log.ReadAll().Single(e => e.StepId == "step.one.q1" && e.Type == ProgressEventTypes.CodeSubmitted);
        Assert.Equal(longOne, onB.Single(e => e.Id == longOne.Id));
        Assert.Contains("goal=15", Print(_b));
        Assert.Empty(await _server.WithDbAsync(db => Task.FromResult(db.Events.Where(e => e.Id == huge.Id).ToList())));

        // Nothing is retried, and later progress still flows.
        _clock.Now = _clock.Now.AddMinutes(1);
        _a.Recorder.SetDailyGoal(25);
        await SettleAsync();
        Assert.Contains("goal=25", Print(_b));
        Assert.Equal(1, _a.Sync.RejectedCount("device-a"));
    }

    [Fact]
    public async Task A_draft_edited_on_one_device_appears_on_the_other_after_sync()
    {
        _a.Drafts.Save("step.sum", "def total(xs):\n    return sum(xs)\n");

        await SettleAsync();

        Assert.Equal("def total(xs):\n    return sum(xs)\n", _b.Drafts.Get("step.sum"));
        Assert.Empty(_a.Drafts.ReadUnsynced());
        Assert.Empty(_b.Drafts.ReadUnsynced());   // what arrived is not sent back
    }

    [Fact]
    public async Task When_both_devices_edit_the_same_step_the_newer_edit_wins_on_both()
    {
        _a.Drafts.Save("step.sum", "mac, earlier");
        _clock.Now = _clock.Now.AddMinutes(3);
        _b.Drafts.Save("step.sum", "phone, later");

        // The Mac syncs last, so it is the one that has to give way even though it pushed first.
        foreach (var d in new[] { _a, _b, _a })
            Assert.True((await d.Sync.SyncAsync()).Succeeded);

        Assert.Equal("phone, later", _a.Drafts.Get("step.sum"));
        Assert.Equal("phone, later", _b.Drafts.Get("step.sum"));
    }

    [Fact]
    public async Task The_newer_edit_wins_whichever_device_syncs_first()
    {
        _b.Drafts.Save("step.sum", "phone, earlier");
        _clock.Now = _clock.Now.AddMinutes(3);
        _a.Drafts.Save("step.sum", "mac, later");

        foreach (var d in new[] { _b, _a, _b })
            Assert.True((await d.Sync.SyncAsync()).Succeeded);

        Assert.Equal("mac, later", _a.Drafts.Get("step.sum"));
        Assert.Equal("mac, later", _b.Drafts.Get("step.sum"));
    }

    [Fact]
    public async Task A_reset_on_one_device_clears_the_other_unless_it_was_edited_after()
    {
        _a.Drafts.Save("step.one", "work");
        _a.Drafts.Save("step.two", "work");
        await SettleAsync();

        _clock.Now = _clock.Now.AddMinutes(1);
        _b.Drafts.Reset("step.one");
        _b.Drafts.Reset("step.two");
        _clock.Now = _clock.Now.AddMinutes(1);
        _a.Drafts.Save("step.two", "edited after the reset");   // the Mac had not synced yet
        await SettleAsync();

        Assert.Null(_a.Drafts.Get("step.one"));
        Assert.Null(_b.Drafts.Get("step.one"));
        Assert.Equal("edited after the reset", _a.Drafts.Get("step.two"));
        Assert.Equal("edited after the reset", _b.Drafts.Get("step.two"));
    }

    [Fact]
    public async Task Drafts_made_while_a_device_was_away_arrive_when_it_comes_back_and_syncing_again_changes_nothing()
    {
        _b.Online = false;
        _b.Drafts.Save("step.offline", "written on the train");
        Assert.Equal(SyncFailure.Unreachable, (await _b.Sync.SyncAsync()).Failure);
        Assert.Single(_b.Drafts.ReadUnsynced());   // still waiting to go

        _b.Online = true;
        await SettleAsync();
        Assert.Equal("written on the train", _a.Drafts.Get("step.offline"));

        foreach (var d in new[] { _a, _b })
        {
            var again = await d.Sync.SyncAsync();
            Assert.Equal((0, 0), (again.DraftsPushed, again.DraftsPulled));
        }
    }

    [Fact]
    public async Task Drafts_and_progress_sync_together_without_disturbing_each_other()
    {
        SyncFixtures.TwoDevicesApart(_a.Recorder, _b.Recorder, _clock);
        _a.Drafts.Save("step.sum", "mine");

        await SettleAsync();

        Assert.Equal(Ids(_a), Ids(_b));
        Assert.Equal(Print(_a), Print(_b));
        Assert.Equal("mine", _b.Drafts.Get("step.sum"));
    }

    [Fact]
    public async Task A_draft_too_big_for_the_server_stays_on_its_device_and_blocks_no_other_draft()
    {
        _a.Drafts.Save("step.huge", new string('x', EventLimits.MaxDataLength + 1));
        _a.Drafts.Save("step.ok", "fine");

        await SettleAsync();

        Assert.Equal("fine", _b.Drafts.Get("step.ok"));
        Assert.Null(_b.Drafts.Get("step.huge"));
        Assert.StartsWith("x", _a.Drafts.Get("step.huge"));   // the learner's work is still there
    }
}
