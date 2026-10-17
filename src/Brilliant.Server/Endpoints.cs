using System.Security.Claims;
using Brilliant.Core.Drafts;
using Brilliant.Core.Progress;
using Brilliant.Core.Sync;
using Brilliant.Server.Data;
using Brilliant.Server.Pairing;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Server;

public static class Endpoints
{
    public const string PairingPolicy = "pairing";

    public static void MapServerEndpoints(this IEndpointRouteBuilder app)
    {
        // Open: a health check has to work without a token, and pairing is how a device gets one.
        app.MapGet("/health", async (ServerDb db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct)
                ? Results.Ok(new { status = "ok" })
                : Results.Json(new { status = "database unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

        app.MapPost("/pair", Pair).RequireRateLimiting(PairingPolicy);

        var authed = app.MapGroup("").RequireAuthorization();
        authed.MapPost("/pairing-codes", MintPairingCode);
        authed.MapGet("/devices", ListDevices);
        authed.MapDelete("/devices/{id:guid}", RevokeDevice);
        authed.MapPost("/events", PushEvents);
        authed.MapGet("/events", PullEvents);
        authed.MapPost("/drafts", PushDrafts);
        authed.MapGet("/drafts", PullDrafts);
        authed.MapPackEndpoints();
        authed.MapFeedbackEndpoints();
    }

    // Serialises pushes so sequence numbers become visible in the order they are handed out. Without it a reader could pull past
    // number 6 while number 5 was still uncommitted, and never see 5.
    public const long PushLock = 2_900_001;

    private static async Task<IResult> PushEvents(PushEventsRequest request, ClaimsPrincipal user, ServerDb db, TimeProvider clock, CancellationToken ct)
    {
        var appId = user.AppId();
        var events = request.Events;
        if (events is null || events.Count > EventLimits.MaxBatch || events.Any(e => e is null))
            return Results.BadRequest(new { error = $"Send between 0 and {EventLimits.MaxBatch} events at a time." });

        // An event that breaks the limits is refused by name; it must not hold up the valid ones sent with it, or one bad event
        // would block a device's whole backlog for good.
        var rejected = new List<RejectedEvent>();
        var valid = new List<ProgressEvent>();
        foreach (var e in events)
        {
            if (EventLimits.Problem(e) is { } why) rejected.Add(new RejectedEvent(e.Id ?? "", why));
            else valid.Add(e);
        }
        if (valid.Count == 0) return Results.Ok(new PushEventsResponse(0, 0, rejected));

        var pushedBy = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({PushLock})", ct);

        var ids = valid.Select(e => e.Id).Distinct().ToList();
        var known = (await db.Events.AsNoTracking().Where(e => e.AppId == appId && ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        var accepted = 0;
        foreach (var e in valid)
        {
            if (!known.Add(e.Id)) continue;
            db.Events.Add(new EventRecord
            {
                AppId = appId, Id = e.Id, DeviceId = e.DeviceId, OccurredAtTicks = e.OccurredAt.UtcTicks, Type = e.Type,
                LessonId = e.LessonId ?? "", StepId = e.StepId ?? "", Data = e.Data, PushedBy = pushedBy, ReceivedAt = now,
            });
            accepted++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new PushEventsResponse(accepted, valid.Count - accepted, rejected));
    }

    // Draft pushes take their own lock for the same reason: sequence numbers are handed out one push at a time.
    public const long DraftLock = 2_900_002;

    private static async Task<IResult> PushDrafts(PushDraftsRequest request, ClaimsPrincipal user, ServerDb db, TimeProvider clock, CancellationToken ct)
    {
        var appId = user.AppId();
        var drafts = request.Drafts;
        if (drafts is null || drafts.Count > DraftLimits.MaxBatch || drafts.Any(d => d is null))
            return Results.BadRequest(new { error = $"Send between 0 and {DraftLimits.MaxBatch} drafts at a time." });

        var rejected = new List<RejectedEvent>();
        var valid = new Dictionary<string, CodeDraft>();
        foreach (var d in drafts)
        {
            if (DraftLimits.Problem(d) is { } why) rejected.Add(new RejectedEvent(d.StepId ?? "", why));
            // Two versions of one step in a batch: only the better can matter.
            else if (!valid.TryGetValue(d.StepId!, out var other) || d.Supersedes(other)) valid[d.StepId!] = d;
        }
        if (valid.Count == 0) return Results.Ok(new PushDraftsResponse(0, 0, rejected));

        var pushedBy = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({DraftLock})", ct);

        var ids = valid.Keys.ToList();
        var held = await db.Drafts.Where(r => r.AppId == appId && ids.Contains(r.StepId)).ToDictionaryAsync(r => r.StepId, ct);
        // Sequence numbers remain global, preserving existing cursors and the unique Seq index.
        // Reads and replacements above/below are scoped; the shared lock makes all assignments visible in order.
        var seq = await db.Drafts.AnyAsync(ct) ? await db.Drafts.MaxAsync(r => r.Seq, ct) : 0;
        var accepted = 0;
        foreach (var d in valid.Values)
        {
            held.TryGetValue(d.StepId, out var row);
            var current = row is null ? null : new CodeDraft(row.StepId, row.Code, new DateTimeOffset(row.UpdatedAtTicks, TimeSpan.Zero));
            if (!d.Supersedes(current)) continue;
            if (row is null) db.Drafts.Add(row = new DraftRecord { AppId = appId, StepId = d.StepId });
            (row.Seq, row.Code, row.UpdatedAtTicks, row.PushedBy, row.ReceivedAt) = (++seq, d.Code, d.UpdatedAt.UtcTicks, pushedBy, now);
            accepted++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new PushDraftsResponse(accepted, valid.Count - accepted, rejected));
    }

    private static async Task<IResult> PullDrafts(long? after, int? limit, ClaimsPrincipal user, ServerDb db, CancellationToken ct)
    {
        var appId = user.AppId();
        var cursor = after ?? 0;
        if (cursor < 0) return Results.BadRequest(new { error = "after must not be negative." });
        var take = Math.Clamp(limit ?? DraftLimits.MaxPullPage, 1, DraftLimits.MaxPullPage);

        var rows = await db.Drafts.AsNoTracking().Where(r => r.AppId == appId && r.Seq > cursor).OrderBy(r => r.Seq).Take(take + 1).ToListAsync(ct);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        var drafts = rows.Select(r => new CodeDraft(r.StepId, r.Code, new DateTimeOffset(r.UpdatedAtTicks, TimeSpan.Zero))).ToList();
        return Results.Ok(new PullDraftsResponse(drafts, rows.Count == 0 ? cursor : rows[^1].Seq, hasMore));
    }

    private static async Task<IResult> PullEvents(long? after, int? limit, ClaimsPrincipal user, ServerDb db, CancellationToken ct)
    {
        var appId = user.AppId();
        var cursor = after ?? 0;
        if (cursor < 0) return Results.BadRequest(new { error = "after must not be negative." });
        var take = Math.Clamp(limit ?? EventLimits.MaxBatch, 1, EventLimits.MaxPullPage);

        var rows = await db.Events.AsNoTracking().Where(e => e.AppId == appId && e.Seq > cursor).OrderBy(e => e.Seq).Take(take + 1).ToListAsync(ct);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        var events = rows.Select(r => new ProgressEvent(r.Id, r.DeviceId, new DateTimeOffset(r.OccurredAtTicks, TimeSpan.Zero), r.Type, r.LessonId, r.StepId, r.Data)).ToList();
        return Results.Ok(new PullEventsResponse(events, rows.Count == 0 ? cursor : rows[^1].Seq, hasMore));
    }

    private static async Task<IResult> Pair(PairRequest request, HttpRequest http, ServerApps apps, PairingCodes codes, ServerDb db, TimeProvider clock, CancellationToken ct)
    {
        if (!apps.Contains(request.AppId)) return Results.BadRequest(new { error = "Unknown app ID. Choose an app configured on this server." });
        var appHeaders = http.Headers[AppSync.AppIdHeader];
        if (appHeaders.Count > 0 && (appHeaders.Count != 1 || appHeaders.ToString() != request.AppId))
            return Results.BadRequest(new { error = "The request header and pairing app ID must match." });
        var name = request.DeviceName?.Trim();
        if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(name) || name.Length > 100)
            return Results.BadRequest(new { error = "A pairing code and a device name (up to 100 characters) are required." });

        if (!await codes.TryRedeemAsync(request.Code, request.AppId, ct))
            return Results.Json(new { error = "That pairing code is not valid. It may have expired or already been used." }, statusCode: StatusCodes.Status401Unauthorized);

        var token = DeviceTokens.Generate();
        var device = new Device { AppId = request.AppId, Id = Guid.NewGuid(), Name = name, TokenHash = DeviceTokens.Hash(token), PairedAt = clock.GetUtcNow() };
        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new PairResponse(device.Id.ToString(), token, device.AppId));
    }

    private static async Task<IResult> MintPairingCode(ClaimsPrincipal user, PairingCodes codes, CancellationToken ct)
    {
        await codes.PruneAsync(user.AppId(), ct);
        var (code, expires) = await codes.MintAsync(user.AppId(), ct);
        return Results.Ok(new PairingCodeResponse(code, expires));
    }

    private static async Task<IResult> ListDevices(ClaimsPrincipal user, ServerDb db, CancellationToken ct)
    {
        var appId = user.AppId();
        var devices = await db.Devices.AsNoTracking().Where(d => d.AppId == appId).OrderBy(d => d.PairedAt).ToListAsync(ct);
        return Results.Ok(devices.Select(d => new DeviceInfo(d.Id.ToString(), d.Name, d.PairedAt, d.LastSeenAt, d.RevokedAt is not null, d.AppId)));
    }

    private static async Task<IResult> RevokeDevice(Guid id, ClaimsPrincipal user, ServerDb db, TimeProvider clock, CancellationToken ct)
    {
        var appId = user.AppId();
        var now = clock.GetUtcNow();
        var changed = await db.Devices.Where(d => d.AppId == appId && d.Id == id && d.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(d => d.RevokedAt, now), ct);
        if (changed == 1) return Results.NoContent();

        // Revoking twice is fine; revoking something that was never paired is not.
        return await db.Devices.AnyAsync(d => d.AppId == appId && d.Id == id, ct) ? Results.NoContent() : Results.NotFound();
    }
}
