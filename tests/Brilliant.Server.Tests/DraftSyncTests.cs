using System.Net.Http.Json;
using Brilliant.Core.Drafts;
using Brilliant.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DraftSyncTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static CodeDraft D(string step, string? code, int minute) => new(step, code, T0.AddMinutes(minute));

    private static async Task<PushDraftsResponse> Push(HttpClient client, params CodeDraft[] drafts)
    {
        var response = await client.PostAsJsonAsync("/drafts", new PushDraftsRequest(drafts));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PushDraftsResponse>())!;
    }

    private static async Task<PullDraftsResponse> Pull(HttpClient client, long after = 0, int? limit = null)
    {
        var response = await client.GetAsync($"/drafts?after={after}" + (limit is null ? "" : $"&limit={limit}"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PullDraftsResponse>())!;
    }

    [Fact]
    public async Task A_pushed_draft_comes_back_exactly_as_written_including_its_ticks()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        var precise = new CodeDraft("step.a", "def f():\n    return 1\n", T0.AddTicks(1234567));

        var pushed = await Push(client, precise);
        var page = await Pull(client);

        Assert.Equal((1, 0), (pushed.Accepted, pushed.Ignored));
        Assert.Equal(precise, Assert.Single(page.Drafts));
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task The_newer_timestamp_wins_even_when_the_older_draft_arrives_last()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        await Push(client, D("step.a", "newer", 10));
        var late = await Push(client, D("step.a", "older, pushed later", 5));

        Assert.Equal((0, 1), (late.Accepted, late.Ignored));
        Assert.Equal("newer", Assert.Single((await Pull(client)).Drafts).Code);
    }

    [Fact]
    public async Task A_newer_draft_replaces_the_older_one_and_there_is_still_one_per_step()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        await Push(client, D("step.a", "v1", 1), D("step.b", "b1", 1));
        await Push(client, D("step.a", "v2", 2));

        var all = (await Pull(client)).Drafts.OrderBy(d => d.StepId).ToList();
        Assert.Equal([("step.a", "v2"), ("step.b", "b1")], all.Select(d => (d.StepId, d.Code)));
    }

    [Fact]
    public async Task A_newer_reset_beats_an_older_edit_and_is_kept_so_other_devices_learn_of_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        await Push(client, D("step.a", "work", 1));
        await Push(client, D("step.a", null, 2));
        await Push(client, D("step.a", "stale edit", 1));

        var draft = Assert.Single((await Pull(client)).Drafts);
        Assert.Equal(("step.a", null), (draft.StepId, draft.Code));
    }

    [Fact]
    public async Task Pushing_the_same_draft_again_changes_nothing_and_does_not_move_it_in_the_cursor_order()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        await Push(client, D("step.a", "x", 1));
        var cursor = (await Pull(client)).Cursor;

        var again = await Push(client, D("step.a", "x", 1));

        Assert.Equal((0, 1), (again.Accepted, again.Ignored));
        Assert.Empty((await Pull(client, cursor)).Drafts);
    }

    [Fact]
    public async Task Two_versions_of_a_step_in_one_push_keep_the_better()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        var pushed = await Push(client, D("step.a", "second", 2), D("step.a", "first", 1));

        Assert.Equal(1, pushed.Accepted);
        Assert.Equal("second", Assert.Single((await Pull(client)).Drafts).Code);
    }

    [Fact]
    public async Task Pulling_from_a_cursor_returns_only_drafts_changed_since_and_pages()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        await Push(client, D("a", "1", 1), D("b", "1", 1), D("c", "1", 1));

        var first = await Pull(client, limit: 2);
        var second = await Pull(client, first.Cursor, limit: 2);
        await Push(client, D("a", "2", 2));   // changed again: moves to the end
        var third = await Pull(client, second.Cursor);
        var nothing = await Pull(client, third.Cursor);

        Assert.Equal(2, first.Drafts.Count);
        Assert.True(first.HasMore);
        Assert.Equal(["c"], second.Drafts.Select(d => d.StepId));
        Assert.False(second.HasMore);
        Assert.Equal([("a", "2")], third.Drafts.Select(d => (d.StepId, d.Code)));
        Assert.Empty(nothing.Drafts);
        Assert.Equal(third.Cursor, nothing.Cursor);
    }

    [Fact]
    public async Task A_draft_over_the_limits_is_refused_by_name_and_the_rest_of_the_push_is_kept()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        var huge = D("step.huge", new string('x', EventLimits.MaxDataLength + 1), 1);

        var pushed = await Push(client, huge, D("step.ok", "fine", 1), D(new string('s', 101), "bad step id", 1));

        Assert.Equal(1, pushed.Accepted);
        Assert.Equal(2, pushed.Rejected!.Count);
        Assert.Equal(["step.ok"], (await Pull(client)).Drafts.Select(d => d.StepId));
    }

    [Fact]
    public async Task Drafts_need_a_device_token()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var anonymous = server.CreateClient();

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/drafts")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/drafts", new PushDraftsRequest([]))).StatusCode);
    }

    [Fact]
    public async Task Too_many_drafts_in_one_push_is_a_bad_request()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        var many = Enumerable.Range(0, DraftLimits.MaxBatch + 1).Select(i => D($"s{i}", "x", 1)).ToList();

        var response = await client.PostAsJsonAsync("/drafts", new PushDraftsRequest(many));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync("/drafts?after=-1")).StatusCode);
    }

    [Fact]
    public async Task Pushes_from_two_devices_at_once_all_land_with_distinct_cursor_positions()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (a, _) = await server.PairDeviceAsync("Mac");
        var (b, _) = await server.PairDeviceAsync("Phone");

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Push(i % 2 == 0 ? a : b, D($"step.{i}", "x", 1))));

        Assert.Equal(20, (await Pull(a)).Drafts.Count);
        Assert.Equal(20, await server.WithDbAsync(db => db.Drafts.Select(d => d.Seq).Distinct().CountAsync()));
    }
}
