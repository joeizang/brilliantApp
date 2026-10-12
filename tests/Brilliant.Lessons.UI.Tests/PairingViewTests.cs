using System.Net;
using System.Net.Http.Json;
using Brilliant.Core.Progress;
using Brilliant.Core.Sync;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The Sync screen: pair with the server using an address and a code, see who you're paired with, forget it.</summary>
public class PairingViewTests : BunitContext
{
    private sealed class MemoryStore : IDeviceCredentialStore
    {
        public DeviceCredentials? Saved;
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) { Saved = credentials; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct = default) { Saved = null; return Task.CompletedTask; }
    }

    private sealed class Stub(HttpStatusCode status, object? body = null) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = body is null ? null : JsonContent.Create(body) });
        }
    }

    private sealed class FailingForget : IDeviceCredentialStore
    {
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult<DeviceCredentials?>(new(new Uri("http://nuc.local:8080/"), "dev-1", "tok-1"));
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => throw new IOException("disk is read-only");
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

    /// <summary>Answers the sync endpoints: nothing to push, nothing to pull, unless told otherwise.</summary>
    private sealed class SyncServer : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool Down;
        public int Calls;
        public TaskCompletionSource? Hold;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Hold is { } hold) await hold.Task;
            if (Down) throw new HttpRequestException("No route to host");
            if (Status != HttpStatusCode.OK) return new HttpResponseMessage(Status);
            return request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PushEventsResponse(0, 0)) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PullEventsResponse([], 0, false)) };
        }
    }

    private static readonly DeviceCredentials Paired = new(new Uri("http://nuc.local:8080/"), "dev-1", "tok-1");

    private readonly MemoryStore _store = new();
    private readonly SyncServer _syncServer = new();
    private readonly MemorySyncState _syncState = new();
    private readonly ManualTimeProvider _clock = new();
    private Stub _server = new(HttpStatusCode.OK, new PairResponse("dev-1", "tok-1"));

    private void Register(IDeviceCredentialStore store)
    {
        Services.AddSingleton(new PairingService(new PairingClient(new HttpClient(_server)), store));
        Services.AddSingleton(new SyncService(new EmptyLog(), "me", store, new SyncClient(new HttpClient(_syncServer)), _syncState, _clock));
        Services.AddSingleton<TimeProvider>(_clock);
    }

    private IRenderedComponent<PairingView> Open()
    {
        Register(_store);
        return Render<PairingView>();
    }

    private static void Fill(IRenderedComponent<PairingView> cut, string address = "nuc.local", string code = "ABCD-2345")
    {
        cut.Find("input.pair-address").Change(address);
        cut.Find("input.pair-code").Change(code);
    }

    [Fact]
    public void An_unpaired_device_is_asked_for_an_address_and_a_code()
    {
        var cut = Open();
        Assert.NotNull(cut.Find("input.pair-address"));
        Assert.NotNull(cut.Find("input.pair-code"));
        Assert.Empty(cut.FindAll(".paired"));
    }

    [Fact]
    public void The_device_name_starts_as_the_machine_name()
    {
        Assert.Equal(Environment.MachineName, Open().Find("input.pair-name").GetAttribute("value"));
    }

    [Fact]
    public void A_host_can_suggest_a_better_device_name()
    {
        Services.AddSingleton(new DeviceLabel("Pixel 9"));   // an Android phone calls itself "localhost"
        Assert.Equal("Pixel 9", Open().Find("input.pair-name").GetAttribute("value"));
    }

    [Fact]
    public void Pairing_stores_the_credentials_and_shows_who_we_are_paired_with()
    {
        var cut = Open();
        Fill(cut);

        cut.Find("form.pair-form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("nuc.local:8443", cut.Find(".paired-server").TextContent));
        Assert.Equal("tok-1", _store.Saved!.Token);
        Assert.Empty(cut.FindAll("form.pair-form"));
    }

    [Fact]
    public void A_device_that_is_already_paired_opens_on_the_paired_screen()
    {
        _store.Saved = new DeviceCredentials(new Uri("http://nuc.local:8080/"), "dev-1", "tok-1");
        var cut = Open();
        cut.WaitForAssertion(() => Assert.Contains("nuc.local:8080", cut.Find(".paired-server").TextContent));
        Assert.Empty(cut.FindAll("input.pair-code"));
    }

    [Fact]
    public void A_wrong_code_says_so_and_stores_nothing()
    {
        _server = new Stub(HttpStatusCode.Unauthorized);
        var cut = Open();
        Fill(cut, code: "WRONG-CODE");

        cut.Find("form.pair-form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("code didn't work", cut.Find(".pair-error").TextContent));
        Assert.Null(_store.Saved);
        Assert.NotNull(cut.Find("input.pair-code"));
    }

    [Fact]
    public void A_bad_address_is_explained_without_calling_the_server()
    {
        var cut = Open();
        Fill(cut, address: "ftp://nope");

        cut.Find("form.pair-form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("server address", cut.Find(".pair-error").TextContent));
        Assert.Equal(0, _server.Calls);
    }

    [Fact]
    public void Leaving_the_fields_empty_asks_for_them_without_calling_the_server()
    {
        var cut = Open();

        cut.Find("form.pair-form").Submit();

        Assert.Contains("Enter", cut.Find(".pair-error").TextContent);
        Assert.Equal(0, _server.Calls);
    }

    [Fact]
    public void An_unreachable_server_is_explained()
    {
        _server = new Stub(HttpStatusCode.BadGateway);
        var cut = Open();
        Fill(cut);

        cut.Find("form.pair-form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Couldn't reach", cut.Find(".pair-error").TextContent));
    }

    private sealed class Untrusted : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established");
    }

    [Fact]
    public void A_certificate_the_device_does_not_trust_says_to_install_the_authority_not_to_check_the_network()
    {
        Services.AddSingleton(new PairingService(new PairingClient(new HttpClient(new Untrusted())), _store));
        Services.AddSingleton(new SyncService(new EmptyLog(), "me", _store, new SyncClient(new HttpClient(_syncServer)), _syncState, _clock));
        Services.AddSingleton<TimeProvider>(_clock);
        var cut = Render<PairingView>();
        Fill(cut);

        cut.Find("form.pair-form").Submit();

        cut.WaitForAssertion(() =>
        {
            var text = cut.Find(".pair-error").TextContent;
            Assert.Contains("doesn't trust the server's certificate", text);
            Assert.DoesNotContain("Couldn't reach", text);
        });
    }

    [Fact]
    public void An_address_typed_with_plain_http_is_turned_away_with_what_to_type()
    {
        var cut = Open();
        Fill(cut, address: "http://nuc.local:8080");

        cut.Find("form.pair-form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("https", cut.Find(".pair-error").TextContent));
        Assert.Equal(0, _server.Calls);
    }

    [Fact]
    public void Forgetting_the_server_clears_it_and_brings_the_form_back()
    {
        _store.Saved = new DeviceCredentials(new Uri("http://nuc.local:8080/"), "dev-1", "tok-1");
        var cut = Open();
        cut.WaitForElement("button.forget").Click();

        Assert.Null(_store.Saved);
        Assert.NotNull(cut.Find("input.pair-code"));
    }

    [Fact]
    public void A_forget_that_fails_says_so_and_stays_paired()
    {
        Register(new FailingForget());
        var cut = Render<PairingView>();

        cut.WaitForElement("button.forget").Click();

        Assert.Contains("Couldn't forget", cut.Find(".forget-error").TextContent);
        Assert.NotNull(cut.Find(".paired"));
    }

    // --- Sync status and "Sync now" ------------------------------------------------------------------------------

    private IRenderedComponent<PairingView> OpenPaired()
    {
        _store.Saved = Paired;
        var cut = Open();
        cut.WaitForElement(".paired");
        return cut;
    }

    [Fact]
    public void A_paired_device_that_has_never_synced_says_so_and_offers_to_sync()
    {
        var cut = OpenPaired();

        Assert.Equal("Not synced yet.", cut.Find(".last-synced").TextContent);
        Assert.Equal("Sync now", cut.Find("button.sync-now").TextContent);
        Assert.Empty(cut.FindAll(".sync-error"));
    }

    [Fact]
    public void With_nothing_held_back_the_sync_screen_says_nothing_about_it()
    {
        Assert.Empty(OpenPaired().FindAll(".sync-held"));
    }

    [Theory]
    [InlineData(1, "1 item from this device is too large for the server and stays on this device only, so other devices won't see it.")]
    [InlineData(2, "2 items from this device are too large for the server and stay on this device only, so other devices won't see them.")]
    public void Events_too_large_for_the_server_are_said_plainly(int held, string expected)
    {
        _syncState.State = new SyncState("dev-1", 5, 5, null, [.. Enumerable.Range(0, held).Select(i => "e" + i)]);

        var cut = OpenPaired();

        Assert.Equal(expected, cut.Find(".sync-held").TextContent.Trim());
    }

    [Fact]
    public void Events_held_back_under_some_other_pairing_are_not_reported_for_this_one()
    {
        _syncState.State = new SyncState("dev-2", 5, 5, null, ["a"]);

        Assert.Empty(OpenPaired().FindAll(".sync-held"));
    }

    [Fact]
    public void Sync_now_syncs_and_shows_when()
    {
        var cut = OpenPaired();

        cut.Find("button.sync-now").Click();

        cut.WaitForAssertion(() => Assert.Equal("Last synced just now.", cut.Find(".last-synced").TextContent));
        Assert.Equal(1, _syncServer.Calls);   // nothing to send, so only the pull
    }

    [Theory]
    [InlineData(0, "Last synced just now.")]
    [InlineData(5, "Last synced 5 min ago.")]
    [InlineData(60 * 3, "Last synced 3 h ago.")]
    public void The_last_synced_time_is_shown_in_plain_words(int minutesAgo, string expected)
    {
        _syncState.State = new SyncState("dev-1", 0, 0, _clock.GetUtcNow().AddMinutes(-minutesAgo));
        var cut = OpenPaired();

        Assert.Equal(expected, cut.Find(".last-synced").TextContent);
    }

    [Fact]
    public void A_sync_time_from_an_earlier_pairing_is_not_shown_as_this_ones()
    {
        _syncState.State = new SyncState("dev-OLD", 5, 5, _clock.GetUtcNow());
        var cut = OpenPaired();

        Assert.Equal("Not synced yet.", cut.Find(".last-synced").TextContent);
    }

    [Fact]
    public void An_unreachable_server_is_reassuring_and_keeps_the_button()
    {
        _syncServer.Down = true;
        var cut = OpenPaired();

        cut.Find("button.sync-now").Click();

        cut.WaitForAssertion(() => Assert.Contains("progress is safe on this device", cut.Find(".sync-error").TextContent));
        Assert.Equal("Sync now", cut.Find("button.sync-now").TextContent);
        Assert.False(cut.Find("button.sync-now").HasAttribute("disabled"));
        Assert.Equal("Not synced yet.", cut.Find(".last-synced").TextContent);
    }

    [Fact]
    public void A_server_that_no_longer_accepts_this_device_says_to_pair_again()
    {
        _syncServer.Status = HttpStatusCode.Unauthorized;
        var cut = OpenPaired();

        cut.Find("button.sync-now").Click();

        cut.WaitForAssertion(() => Assert.Contains("pair again", cut.Find(".sync-error").TextContent));
    }

    [Fact]
    public void A_server_error_is_explained_without_alarm()
    {
        _syncServer.Status = HttpStatusCode.InternalServerError;
        var cut = OpenPaired();

        cut.Find("button.sync-now").Click();

        cut.WaitForAssertion(() => Assert.Contains("ran into a problem", cut.Find(".sync-error").TextContent));
        Assert.Contains("500", cut.Find(".sync-detail").TextContent);
    }

    [Fact]
    public void A_later_good_sync_clears_the_earlier_complaint()
    {
        _syncServer.Down = true;
        var cut = OpenPaired();
        cut.Find("button.sync-now").Click();
        cut.WaitForElement(".sync-error");

        _syncServer.Down = false;
        cut.Find("button.sync-now").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".sync-error")));
        Assert.Equal("Last synced just now.", cut.Find(".last-synced").TextContent);
    }

    [Fact]
    public void While_a_sync_runs_the_button_says_so_and_cannot_be_pressed_again()
    {
        _syncServer.Hold = new TaskCompletionSource();
        var cut = OpenPaired();

        cut.Find("button.sync-now").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Syncing…", cut.Find("button.sync-now").TextContent);
            Assert.True(cut.Find("button.sync-now").HasAttribute("disabled"));
        });

        _syncServer.Hold.SetResult();
        cut.WaitForAssertion(() => Assert.Equal("Sync now", cut.Find("button.sync-now").TextContent));
    }

    [Fact]
    public async Task A_sync_started_elsewhere_in_the_app_updates_the_open_screen()
    {
        var cut = OpenPaired();
        var sync = Services.GetRequiredService<SyncService>();

        await Task.Run(() => sync.SyncAsync());   // from a thread that is not the renderer's, as the periodic one is

        cut.WaitForAssertion(() => Assert.Equal("Last synced just now.", cut.Find(".last-synced").TextContent));
    }

    [Fact]
    public async Task Leaving_the_screen_stops_it_listening_to_the_sync()
    {
        var cut = OpenPaired();
        var sync = Services.GetRequiredService<SyncService>();

        Dispose();   // tears the page down, as leaving the screen does

        // Would throw on a disposed renderer if the screen were still subscribed.
        var result = await sync.SyncAsync();
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Pairing_starts_a_first_sync_straight_away()
    {
        var cut = Open();
        Fill(cut);

        cut.Find("form.pair-form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("Last synced just now.", cut.Find(".last-synced").TextContent));
        Assert.True(_syncServer.Calls > 0);
    }
}
