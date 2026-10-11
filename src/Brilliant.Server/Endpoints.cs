using System.Security.Claims;
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
    }

    private static async Task<IResult> Pair(PairRequest request, PairingCodes codes, ServerDb db, TimeProvider clock, CancellationToken ct)
    {
        var name = request.DeviceName?.Trim();
        if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(name) || name.Length > 100)
            return Results.BadRequest(new { error = "A pairing code and a device name (up to 100 characters) are required." });

        if (!await codes.TryRedeemAsync(request.Code, ct))
            return Results.Json(new { error = "That pairing code is not valid. It may have expired or already been used." }, statusCode: StatusCodes.Status401Unauthorized);

        var token = DeviceTokens.Generate();
        var device = new Device { Id = Guid.NewGuid(), Name = name, TokenHash = DeviceTokens.Hash(token), PairedAt = clock.GetUtcNow() };
        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new PairResponse(device.Id.ToString(), token));
    }

    private static async Task<IResult> MintPairingCode(PairingCodes codes, CancellationToken ct)
    {
        await codes.PruneAsync(ct);
        var (code, expires) = await codes.MintAsync(ct);
        return Results.Ok(new PairingCodeResponse(code, expires));
    }

    private static async Task<IResult> ListDevices(ServerDb db, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().OrderBy(d => d.PairedAt).ToListAsync(ct);
        return Results.Ok(devices.Select(d => new DeviceInfo(d.Id.ToString(), d.Name, d.PairedAt, d.LastSeenAt, d.RevokedAt is not null)));
    }

    private static async Task<IResult> RevokeDevice(Guid id, ServerDb db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var changed = await db.Devices.Where(d => d.Id == id && d.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(d => d.RevokedAt, now), ct);
        if (changed == 1) return Results.NoContent();

        // Revoking twice is fine; revoking something that was never paired is not.
        return await db.Devices.AnyAsync(d => d.Id == id, ct) ? Results.NoContent() : Results.NotFound();
    }
}
