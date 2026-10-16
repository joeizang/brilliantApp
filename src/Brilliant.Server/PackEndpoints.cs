using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using Brilliant.Core.Content;
using Brilliant.Core.Sync;
using Brilliant.Server.Data;
using Brilliant.Server.Pairing;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Server;

/// <summary>
/// Content Packs: the authoring tool publishes them here and devices download newer ones (docs/content-distribution.md).
/// Every version is kept whole and never changed, so a device that has a version has exactly what the server has.
/// </summary>
public static class PackEndpoints
{
    // Serialises publishes so "is this the newest version" can't be answered for two at once.
    public const long PackLock = 2_900_003;

    /// <summary>A pack unpacks to a few hundred KB; this stops a small zip that expands to gigabytes.</summary>
    private const long MaxUnpackedBytes = 100L * 1024 * 1024;

    public static void MapPackEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/packs", Publish);
        group.MapGet("/packs", List);
        group.MapGet("/packs/{packId}/{version}", Download);
    }

    private static async Task<IResult> Publish(HttpRequest request, ClaimsPrincipal user, ServerDb db, TimeProvider clock, CancellationToken ct)
    {
        var appId = user.AppId();
        var bytes = await ReadBodyAsync(request, ct);
        if (bytes is null) return Results.Json(new { error = $"The pack is over {PackLimits.MaxBytes / (1024 * 1024)} MB." }, statusCode: StatusCodes.Status413PayloadTooLarge);
        if (bytes.Length == 0) return Refuse("Send the pack (a zip) as the request body.");

        ContentGraph pack;
        try
        {
            using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
                if (zip.Entries.Sum(e => e.Length) > MaxUnpackedBytes) return Refuse("The pack unpacks to more than 100 MB.");
            pack = ContentPackFormat.Load(new MemoryStream(bytes));
        }
        catch (ContentPackException e) { return Refuse(e.Message); }
        catch (Exception e) when (e is InvalidDataException or NullReferenceException or ArgumentException) { return Refuse("That is not a usable Content Pack."); }

        var manifest = pack.Manifest;
        if (!PackIds.IsValid(manifest.PackId))
            return Refuse($"The pack ID must be 1 to {PackIds.MaxLength} letters, digits, dots, dashes or underscores (got '{manifest.PackId}').");
        if (!PackVersion.TryParse(manifest.Version, out var version))
            return Refuse($"The pack version must look like 1.2.3 (got '{manifest.Version}').");

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var publishedBy = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({PackLock})", ct);

        var versions = await db.Packs.AsNoTracking().Where(p => p.AppId == appId && p.PackId == manifest.PackId)
            .Select(p => new { p.Major, p.Minor, p.Patch, p.Sha256 }).ToListAsync(ct);
        var latest = versions.Select(v => new PackVersion(v.Major, v.Minor, v.Patch)).DefaultIfEmpty().Max();
        var existing = versions.FirstOrDefault(v => new PackVersion(v.Major, v.Minor, v.Patch) == version);

        if (existing is not null)
        {
            if (existing.Sha256 != sha)
                return Results.Json(new { error = $"{manifest.PackId} {version} is already published with different contents. Raise the version in pack.yaml." }, statusCode: StatusCodes.Status409Conflict);
            var same = await db.Packs.AsNoTracking().SingleAsync(p => p.AppId == appId && p.PackId == manifest.PackId && p.Major == version.Major && p.Minor == version.Minor && p.Patch == version.Patch, ct);
            return Results.Ok(new PublishPackResponse(Info(same), Created: false, []));
        }
        if (versions.Count > 0 && version < latest)
            return Results.Json(new { error = $"{manifest.PackId} {latest} is already published, which is newer than {version}. Versions only go up: to go back, publish the older content under a higher version." }, statusCode: StatusCodes.Status409Conflict);

        // What the previous version had that this one doesn't: progress on those IDs is kept but stops showing.
        var removed = new List<string>();
        if (versions.Count > 0)
        {
            var previous = await db.Packs.AsNoTracking().SingleAsync(p => p.AppId == appId && p.PackId == manifest.PackId && p.Major == latest.Major && p.Minor == latest.Minor && p.Patch == latest.Patch, ct);
            try { removed = ContentPackFormat.Load(new MemoryStream(previous.Content)).Ids.Except(pack.Ids).Order(StringComparer.Ordinal).ToList(); }
            catch (ContentPackException) { /* an old format the server can no longer read: nothing to compare */ }
        }

        var record = new PackRecord
        {
            AppId = appId, PackId = manifest.PackId, Major = version.Major, Minor = version.Minor, Patch = version.Patch, FormatVersion = manifest.FormatVersion,
            Sha256 = sha, Content = bytes, Size = bytes.LongLength, PublishedBy = publishedBy, PublishedAt = clock.GetUtcNow(),
        };
        db.Packs.Add(record);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new PublishPackResponse(Info(record), Created: true, removed));
    }

    private static async Task<IResult> List(ClaimsPrincipal user, ServerDb db, CancellationToken ct)
    {
        var appId = user.AppId();
        var rows = await db.Packs.AsNoTracking().Where(p => p.AppId == appId)
            .Select(p => new PackRecord { AppId = p.AppId, PackId = p.PackId, Major = p.Major, Minor = p.Minor, Patch = p.Patch, FormatVersion = p.FormatVersion, Sha256 = p.Sha256, Size = p.Size, PublishedAt = p.PublishedAt })
            .ToListAsync(ct);
        var newest = rows.GroupBy(r => r.PackId).Select(g => g.OrderByDescending(r => r.Major).ThenByDescending(r => r.Minor).ThenByDescending(r => r.Patch).First())
            .OrderBy(r => r.PackId, StringComparer.Ordinal).Select(Info).ToList();
        return Results.Ok(new PackListResponse(newest));
    }

    private static async Task<IResult> Download(string packId, string version, ClaimsPrincipal user, ServerDb db, CancellationToken ct)
    {
        var appId = user.AppId();
        if (!PackIds.IsValid(packId) || !PackVersion.TryParse(version, out var v)) return Results.NotFound();
        var pack = await db.Packs.AsNoTracking().SingleOrDefaultAsync(p => p.AppId == appId && p.PackId == packId && p.Major == v.Major && p.Minor == v.Minor && p.Patch == v.Patch, ct);
        return pack is null ? Results.NotFound() : Results.File(pack.Content, "application/zip", $"{packId}_{v}.zip", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{pack.Sha256}\""));
    }

    private static PackInfo Info(PackRecord p) =>
        new(p.PackId, new PackVersion(p.Major, p.Minor, p.Patch).ToString(), p.FormatVersion, p.Sha256, p.Size, p.PublishedAt, p.AppId);

    private static IResult Refuse(string reason) => Results.BadRequest(new { error = reason });

    /// <summary>The request body, or null if it is over <see cref="PackLimits.MaxBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > PackLimits.MaxBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > PackLimits.MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
