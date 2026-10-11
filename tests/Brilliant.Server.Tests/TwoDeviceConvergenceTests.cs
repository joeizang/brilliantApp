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
        public ProgressRecorder Recorder { get; }
        public SyncService Sync { get; }
        public bool Online { get; set; } = true;

        public Device(string name, HttpMessageHandler server, string token, SyncFixtures.Clock clock)
        {
            Recorder = new ProgressRecorder(Log, name, clock);
            var link = new HttpClient(new Link(this, server));
            Sync = new SyncService(Log, name, new MemoryCredentials(new DeviceCredentials(new Uri("http://localhost/"), name, token)),
                new SyncClient(link), new MemoryState(), clock);
        }

        public void Dispose() => Log.Dispose();

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
}
