using System.Net;
using System.Net.Http.Json;
using Brilliant.Core.Progress;
using Brilliant.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class EventSyncTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static ProgressEvent Evt(string id, string device = "mac", int minute = 0, string type = ProgressEventTypes.StepCompleted, string? data = null) =>
        new(id, device, T0.AddMinutes(minute), type, "lesson.l", "step.a", data);

    private static Task<HttpResponseMessage> Push(HttpClient client, params ProgressEvent[] events) =>
        client.PostAsJsonAsync("/events", new PushEventsRequest(events));

    private static async Task<PullEventsResponse> Pull(HttpClient client, long after = 0, int? limit = null)
    {
        var response = await client.GetAsync($"/events?after={after}" + (limit is null ? "" : $"&limit={limit}"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PullEventsResponse>())!;
    }

    [Fact]
    public async Task Pushed_events_come_back_in_the_order_they_arrived_and_exactly_as_recorded()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        // Ticks finer than a microsecond: Postgres timestamps would lose them, and two devices must order events identically.
        var precise = Evt("e2", minute: 1) with { OccurredAt = T0.AddTicks(1234567) };

        var response = await Push(client, Evt("e1"), precise);
        var pushed = (await response.Content.ReadFromJsonAsync<PushEventsResponse>())!;
        var page = await Pull(client);

        Assert.Equal(new PushEventsResponse(2, 0), pushed);
        Assert.Equal(["e1", "e2"], page.Events.Select(e => e.Id));
        Assert.Equal(precise, page.Events[1]);
        Assert.False(page.HasMore);
        Assert.True(page.Cursor > 0);
    }

    [Fact]
    public async Task Pushing_the_same_events_again_changes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        await Push(client, Evt("e1"), Evt("e2"));

        var again = (await (await Push(client, Evt("e2"), Evt("e3"), Evt("e3"))).Content.ReadFromJsonAsync<PushEventsResponse>())!;

        Assert.Equal(new PushEventsResponse(1, 2), again);   // e3 twice in one batch counts once
        Assert.Equal(["e1", "e2", "e3"], (await Pull(client)).Events.Select(e => e.Id));
    }

    [Fact]
    public async Task Pulling_from_a_cursor_returns_only_what_came_after_and_pages()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        await Push(client, Evt("e1"), Evt("e2"), Evt("e3"), Evt("e4"), Evt("e5"));

        var first = await Pull(client, limit: 2);
        var second = await Pull(client, first.Cursor, limit: 2);
        var third = await Pull(client, second.Cursor, limit: 2);
        var nothing = await Pull(client, third.Cursor);

        Assert.Equal(["e1", "e2"], first.Events.Select(e => e.Id));
        Assert.True(first.HasMore);
        Assert.Equal(["e3", "e4"], second.Events.Select(e => e.Id));
        Assert.True(second.HasMore);
        Assert.Equal(["e5"], third.Events.Select(e => e.Id));
        Assert.False(third.HasMore);
        Assert.Empty(nothing.Events);
        Assert.Equal(third.Cursor, nothing.Cursor);   // an empty pull leaves the cursor where it was
    }

    [Fact]
    public async Task Every_device_sees_the_same_log_whoever_pushed()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (mac, _) = await server.PairDeviceAsync("Mac");
        var (phone, _) = await server.PairDeviceAsync("Phone");
        await Push(mac, Evt("m1", "mac"));
        await Push(phone, Evt("p1", "phone"));

        Assert.Equal(["m1", "p1"], (await Pull(mac)).Events.Select(e => e.Id));
        Assert.Equal(["m1", "p1"], (await Pull(phone)).Events.Select(e => e.Id));
    }

    [Fact]
    public async Task A_batch_with_one_bad_event_stores_none_of_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        var response = await Push(client, Evt("good"), Evt("bad") with { Type = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await Pull(client)).Events);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task An_event_needs_an_id(string id)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await Push(client, Evt(id))).StatusCode);
    }

    [Fact]
    public async Task Events_and_batches_over_the_limits_are_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        var tooLong = Evt(new string('x', EventLimits.MaxIdLength + 1));
        var bigData = Evt("big", data: new string('d', EventLimits.MaxDataLength + 1));
        var tooMany = Enumerable.Range(0, EventLimits.MaxBatch + 1).Select(i => Evt("n" + i)).ToArray();

        Assert.Equal(HttpStatusCode.BadRequest, (await Push(client, tooLong)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Push(client, bigData)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Push(client, tooMany)).StatusCode);
        Assert.Empty((await Pull(client)).Events);
    }

    [Fact]
    public async Task A_negative_cursor_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/events?after=-1")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_device_can_neither_push_nor_pull()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (keeper, _) = await server.PairDeviceAsync("Keeper");
        var (lost, lostDevice) = await server.PairDeviceAsync("Lost");
        await keeper.DeleteAsync($"/devices/{lostDevice.DeviceId}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Push(lost, Evt("e1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await lost.GetAsync("/events")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_pushes_never_hide_an_event_from_a_reader_walking_the_cursor()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (writerA, _) = await server.PairDeviceAsync("A");
        var (writerB, _) = await server.PairDeviceAsync("B");
        var (reader, _) = await server.PairDeviceAsync("Reader");

        var done = false;
        var seen = new List<string>();
        var readerTask = Task.Run(async () =>
        {
            long cursor = 0;
            while (true)
            {
                var finishing = Volatile.Read(ref done);
                var page = await Pull(reader, cursor, limit: 7);
                seen.AddRange(page.Events.Select(e => e.Id));
                cursor = page.Cursor;
                if (finishing && !page.HasMore) return;
            }
        });

        var writers = new[] { writerA, writerB }.SelectMany((w, writer) =>
            Enumerable.Range(0, 6).Select(batch => Task.Run(async () =>
            {
                var events = Enumerable.Range(0, 10).Select(i => Evt($"w{writer}-b{batch}-{i}", $"dev{writer}")).ToArray();
                (await Push(w, events)).EnsureSuccessStatusCode();
            }))).ToArray();
        await Task.WhenAll(writers);
        Volatile.Write(ref done, true);
        await readerTask;

        Assert.Equal(120, seen.Count);
        Assert.Equal(120, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_push_waits_its_turn_behind_the_lock_that_keeps_sequence_numbers_visible_in_order()
    {
        // The race itself (a reader passing number 6 while 5 is still uncommitted) can't be forced from outside, so this holds the
        // push lock and shows that a push queues behind it, then goes through the moment it is released.
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        var connectionString = await server.WithDbAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        await using var holder = new Npgsql.NpgsqlConnection(connectionString);
        await holder.OpenAsync();
        await new Npgsql.NpgsqlCommand($"SELECT pg_advisory_lock({Endpoints.PushLock})", holder).ExecuteNonQueryAsync();

        var push = Push(client, Evt("queued"));
        await Task.Delay(500);
        Assert.False(push.IsCompleted);
        Assert.Empty((await Pull(client)).Events);

        await new Npgsql.NpgsqlCommand($"SELECT pg_advisory_unlock({Endpoints.PushLock})", holder).ExecuteNonQueryAsync();
        (await push.WaitAsync(TimeSpan.FromSeconds(10))).EnsureSuccessStatusCode();
        Assert.Equal(["queued"], (await Pull(client)).Events.Select(e => e.Id));
    }

    [Fact]
    public async Task The_events_table_enforces_unique_ids_even_without_the_endpoint()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        await Push(client, Evt("e1"));

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => server.WithDbAsync(async db =>
        {
            db.Events.Add(new Data.EventRecord { Id = "e1", DeviceId = "x", Type = "t" });
            return await db.SaveChangesAsync();
        }));
        Assert.NotNull(ex);
    }
}
