using System.Security.Cryptography;
using Brilliant.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Server.Pairing;

public sealed class PairingOptions
{
    public int CodeLifetimeMinutes { get; set; } = 15;

    /// <summary>Pairing attempts allowed per client address per minute. Codes are short, so guessing has to be slow.</summary>
    public int AttemptsPerMinute { get; set; } = 10;
}

/// <summary>Mints and redeems one-time pairing codes. A code is eight characters from an alphabet without look-alikes, shown as XXXX-XXXX.</summary>
public sealed class PairingCodes(ServerDb db, TimeProvider clock, Microsoft.Extensions.Options.IOptions<PairingOptions> options)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<(string Code, DateTimeOffset ExpiresAt)> MintAsync(CancellationToken ct = default)
    {
        var raw = string.Create(8, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(options.Value.CodeLifetimeMinutes);
        db.PairingCodes.Add(new PairingCode { Hash = DeviceTokens.Hash(raw), CreatedAt = now, ExpiresAt = expires });
        await db.SaveChangesAsync(ct);
        return ($"{raw[..4]}-{raw[4..]}", expires);
    }

    /// <summary>
    /// Spends the code if it exists, hasn't expired and hasn't been used. The spend is one conditional UPDATE, so two devices
    /// racing with the same code can't both win.
    /// </summary>
    public async Task<bool> TryRedeemAsync(string code, CancellationToken ct = default)
    {
        var hash = DeviceTokens.Hash(Normalise(code));
        var now = clock.GetUtcNow();
        var spent = await db.PairingCodes
            .Where(c => c.Hash == hash && c.UsedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);
        return spent == 1;
    }

    /// <summary>Spent and expired codes carry no value; drop the ones that are a day past their use.</summary>
    public Task PruneAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetUtcNow().AddDays(-1);
        return db.PairingCodes.Where(c => c.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private static string Normalise(string code) => new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
