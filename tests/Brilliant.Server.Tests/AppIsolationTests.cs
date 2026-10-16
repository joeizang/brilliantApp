using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Brilliant.Core.Apps;
using Brilliant.Core.Content;
using Brilliant.Core.Drafts;
using Brilliant.Core.Progress;
using Brilliant.Core.Sync;

namespace Brilliant.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AppIsolationTests(PostgresFixture postgres)
{
    private const string OtherApp = "com.joeizang.brilliantapp.runtimecheck";
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Two_apps_pair_the_same_device_name_and_list_only_their_own_devices()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (dsa, first) = await server.PairDeviceAsync("My Mac");
        var (other, second) = await server.PairDeviceAsync("My Mac", OtherApp);

        Assert.Equal(AppStoragePaths.DsaAppId, first.AppId);
        Assert.Equal(OtherApp, second.AppId);
        Assert.NotEqual(first.Token, second.Token);
        var dsaDevice = Assert.Single((await dsa.GetFromJsonAsync<List<DeviceInfo>>("/devices"))!);
        var otherDevice = Assert.Single((await other.GetFromJsonAsync<List<DeviceInfo>>("/devices"))!);
        Assert.Equal((first.DeviceId, AppStoragePaths.DsaAppId), (dsaDevice.Id, dsaDevice.AppId));
        Assert.Equal((second.DeviceId, OtherApp), (otherDevice.Id, otherDevice.AppId));

        Assert.Equal(HttpStatusCode.NotFound, (await dsa.DeleteAsync($"/devices/{second.DeviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await other.DeleteAsync($"/devices/{second.DeviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await dsa.GetAsync("/devices")).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/devices")]
    [InlineData("DELETE", "/devices/6f8c0d9e-2b1a-4c3d-8e5f-0a1b2c3d4e5f")]
    [InlineData("GET", "/events")]
    [InlineData("POST", "/events")]
    [InlineData("GET", "/drafts")]
    [InlineData("POST", "/drafts")]
    [InlineData("GET", "/packs")]
    [InlineData("POST", "/packs")]
    [InlineData("GET", "/packs/shared.pack/1.0.0")]
    [InlineData("POST", "/pairing-codes")]
    public async Task A_valid_token_cannot_select_another_app_or_omit_its_non_default_app(string method, string path)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (_, dsa) = await server.PairDeviceAsync();
        var (_, other) = await server.PairDeviceAsync(appId: OtherApp);

        foreach (var client in new[] { server.Authorised(dsa.Token, OtherApp), server.Authorised(other.Token) })
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path))).StatusCode);
    }

    [Fact]
    public async Task Redeeming_a_code_for_the_wrong_app_fails_without_consuming_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var code = await server.MintCodeAsync(OtherApp);
        var wrong = await server.CreateClient().PostAsJsonAsync("/pair", new PairRequest(code, "Mac"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Brilliant-App-Id", OtherApp);
        var right = await client.PostAsJsonAsync("/pair", new PairRequest(code, "Mac", OtherApp));
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal(OtherApp, (await right.Content.ReadFromJsonAsync<PairResponse>())!.AppId);
    }

    [Fact]
    public async Task A_pairing_header_cannot_override_the_app_in_the_request_body()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var code = await server.MintCodeAsync();
        var mismatched = server.CreateClient();
        mismatched.DefaultRequestHeaders.Add("X-Brilliant-App-Id", OtherApp);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await mismatched.PostAsJsonAsync("/pair", new PairRequest(code, "Mac"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await server.CreateClient().PostAsJsonAsync("/pair", new PairRequest(code, "Mac"))).StatusCode);
    }

    [Fact]
    public async Task An_authenticated_device_mints_codes_only_for_its_own_app()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (other, _) = await server.PairDeviceAsync(appId: OtherApp);
        var minted = await other.PostAsync("/pairing-codes", null);
        minted.EnsureSuccessStatusCode();
        var code = (await minted.Content.ReadFromJsonAsync<PairingCodeResponse>())!.Code;

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await server.CreateClient().PostAsJsonAsync("/pair", new PairRequest(code, "Phone"))).StatusCode);
        var pairing = server.CreateClient();
        pairing.DefaultRequestHeaders.Add("X-Brilliant-App-Id", OtherApp);
        Assert.Equal(HttpStatusCode.OK,
            (await pairing.PostAsJsonAsync("/pair", new PairRequest(code, "Phone", OtherApp))).StatusCode);
    }

    [Fact]
    public async Task Identical_event_ids_deduplicate_within_each_app_and_pagination_never_leaks_the_other_log()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (dsa, _) = await server.PairDeviceAsync();
        var (other, _) = await server.PairDeviceAsync(appId: OtherApp);
        var shared = new ProgressEvent("same.event", "mac", T0, ProgressEventTypes.StepCompleted, "lesson.a", "step.a", "dsa");
        var dsaPush = await Post<PushEventsResponse>(dsa, "/events", new PushEventsRequest([shared]));
        var otherPush = await Post<PushEventsResponse>(other, "/events", new PushEventsRequest([shared with { Data = "other" }]));
        Assert.Equal(1, dsaPush.Accepted);
        Assert.Equal(1, otherPush.Accepted);
        Assert.Equal(1, (await Post<PushEventsResponse>(dsa, "/events", new PushEventsRequest([shared]))).Duplicates);
        Assert.Equal(1, (await Post<PushEventsResponse>(other, "/events", new PushEventsRequest([shared]))).Duplicates);
        await Post<PushEventsResponse>(dsa, "/events", new PushEventsRequest([shared with { Id = "dsa.only" }]));
        await Post<PushEventsResponse>(other, "/events", new PushEventsRequest([shared with { Id = "other.only", Data = "other" }]));

        foreach (var (client, suffix, data) in new[] { (dsa, "dsa.only", "dsa"), (other, "other.only", "other") })
        {
            var first = (await client.GetFromJsonAsync<PullEventsResponse>("/events?limit=1"))!;
            Assert.True(first.HasMore);
            Assert.Equal("same.event", Assert.Single(first.Events).Id);
            Assert.Equal(data, first.Events[0].Data);
            var second = (await client.GetFromJsonAsync<PullEventsResponse>($"/events?limit=1&after={first.Cursor}"))!;
            Assert.Equal(suffix, Assert.Single(second.Events).Id);
            Assert.False(second.HasMore);
            Assert.Empty((await client.GetFromJsonAsync<PullEventsResponse>($"/events?after={second.Cursor}"))!.Events);
        }
    }

    [Fact]
    public async Task Draft_updates_and_resets_are_independent_even_for_identical_step_ids()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (dsa, _) = await server.PairDeviceAsync();
        var (other, _) = await server.PairDeviceAsync(appId: OtherApp);
        await Post<PushDraftsResponse>(dsa, "/drafts", new PushDraftsRequest([new CodeDraft("same.step", "dsa", T0)]));
        await Post<PushDraftsResponse>(other, "/drafts", new PushDraftsRequest([new CodeDraft("same.step", "other", T0)]));
        var dsaPage = (await dsa.GetFromJsonAsync<PullDraftsResponse>("/drafts?limit=1"))!;
        var otherPage = (await other.GetFromJsonAsync<PullDraftsResponse>("/drafts?limit=1"))!;
        Assert.Equal("dsa", Assert.Single(dsaPage.Drafts).Code);
        Assert.Equal("other", Assert.Single(otherPage.Drafts).Code);

        await Post<PushDraftsResponse>(dsa, "/drafts", new PushDraftsRequest([new CodeDraft("same.step", null, T0.AddMinutes(1))]));
        await Post<PushDraftsResponse>(other, "/drafts", new PushDraftsRequest([new CodeDraft("same.step", "other updated", T0.AddMinutes(2))]));
        Assert.Null(Assert.Single((await dsa.GetFromJsonAsync<PullDraftsResponse>($"/drafts?after={dsaPage.Cursor}"))!.Drafts).Code);
        Assert.Equal("other updated", Assert.Single((await other.GetFromJsonAsync<PullDraftsResponse>($"/drafts?after={otherPage.Cursor}"))!.Drafts).Code);
    }

    [Fact]
    public async Task Draft_pagination_counts_only_rows_in_the_selected_app()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (dsa, _) = await server.PairDeviceAsync();
        var (other, _) = await server.PairDeviceAsync(appId: OtherApp);
        foreach (var i in Enumerable.Range(1, 3))
        {
            await Post<PushDraftsResponse>(dsa, "/drafts", new PushDraftsRequest([new CodeDraft($"dsa.{i}", "dsa", T0)]));
            await Post<PushDraftsResponse>(other, "/drafts", new PushDraftsRequest([new CodeDraft($"other.{i}", "other", T0)]));
        }
        foreach (var (client, prefix) in new[] { (dsa, "dsa"), (other, "other") })
        {
            long cursor = 0;
            foreach (var i in Enumerable.Range(1, 3))
            {
                var page = (await client.GetFromJsonAsync<PullDraftsResponse>($"/drafts?limit=1&after={cursor}"))!;
                Assert.Equal($"{prefix}.{i}", Assert.Single(page.Drafts).StepId);
                Assert.Equal(i < 3, page.HasMore);
                cursor = page.Cursor;
            }
            Assert.Empty((await client.GetFromJsonAsync<PullDraftsResponse>($"/drafts?after={cursor}"))!.Drafts);
        }
    }

    [Fact]
    public async Task Pack_versions_bytes_removed_ids_and_downloads_are_scoped_to_the_app()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (dsa, _) = await server.PairDeviceAsync();
        var (other, _) = await server.PairDeviceAsync(appId: OtherApp);
        var dsaBytes = Pack("1.0.0", "dsa.step", "dsa.removed");
        var otherBytes = Pack("1.0.0", "other.step", "other.removed");
        Assert.True((await Publish(dsa, dsaBytes)).Created);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/packs/shared.pack/1.0.0")).StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<PackListResponse>("/packs"))!.Packs);
        Assert.True((await Publish(other, otherBytes)).Created);
        Assert.Equal(dsaBytes, await dsa.GetByteArrayAsync("/packs/shared.pack/1.0.0"));
        Assert.Equal(otherBytes, await other.GetByteArrayAsync("/packs/shared.pack/1.0.0"));

        var dsaNew = await Publish(dsa, Pack("1.1.0", "dsa.step"));
        Assert.Equal(["dsa.removed"], dsaNew.RemovedIds);
        Assert.Equal("1.0.0", Assert.Single((await other.GetFromJsonAsync<PackListResponse>("/packs"))!.Packs).Version);
        var otherNew = await Publish(other, Pack("1.1.0", "other.step"));
        Assert.Equal(["other.removed"], otherNew.RemovedIds);
        Assert.Equal(AppStoragePaths.DsaAppId, Assert.Single((await dsa.GetFromJsonAsync<PackListResponse>("/packs"))!.Packs).AppId);
        Assert.Equal(OtherApp, Assert.Single((await other.GetFromJsonAsync<PackListResponse>("/packs"))!.Packs).AppId);
    }

    [Fact]
    public async Task Unknown_apps_cannot_pair_or_use_an_existing_token()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        const string unknown = "com.example.unregistered";
        var code = await server.MintCodeAsync();
        var pairing = server.CreateClient();
        pairing.DefaultRequestHeaders.Add("X-Brilliant-App-Id", unknown);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await pairing.PostAsJsonAsync("/pair", new PairRequest(code, "Mac", unknown))).StatusCode);
        var (_, dsa) = await server.PairDeviceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Authorised(dsa.Token, unknown).GetAsync("/devices")).StatusCode);
        // Refusing an unknown app did not redeem a real DSA code.
        Assert.Equal(HttpStatusCode.OK,
            (await server.CreateClient().PostAsJsonAsync("/pair", new PairRequest(code, "Mac"))).StatusCode);
    }

    private static async Task<T> Post<T>(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static byte[] Pack(string version, params string[] steps)
    {
        var lesson = new Lesson("lesson.a", "A", "track.a", steps.Select(id => (Step)new ExplainStep(id, id, "body", [], null)).ToList());
        using var stream = new MemoryStream();
        ContentPackFormat.Write(stream, new PackManifest("shared.pack", version, ContentPackFormat.CurrentFormatVersion),
            [new Track("track.a", "A", [lesson.Id])], [lesson]);
        return stream.ToArray();
    }

    private static async Task<PublishPackResponse> Publish(HttpClient client, byte[] bytes)
    {
        var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var response = await client.PostAsync("/packs", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PublishPackResponse>())!;
    }
}
