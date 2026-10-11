using System.Security.Claims;
using System.Text.Encodings.Web;
using Brilliant.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Brilliant.Server.Pairing;

public static class DeviceTokenDefaults
{
    public const string Scheme = "DeviceToken";
}

/// <summary>Authenticates "Authorization: Bearer &lt;device token&gt;" against the paired, un-revoked devices.</summary>
public sealed class DeviceTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ServerDb db, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly TimeSpan LastSeenGranularity = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

        var token = header[prefix.Length..].Trim();
        if (token.Length == 0) return AuthenticateResult.Fail("Empty token.");

        var hash = DeviceTokens.Hash(token);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.TokenHash == hash, Context.RequestAborted);
        if (device is null) return AuthenticateResult.Fail("Unknown token.");
        if (device.RevokedAt is not null) return AuthenticateResult.Fail("This device has been revoked.");

        // Recorded at most once a minute, so a busy sync doesn't turn every request into a write.
        var now = clock.GetUtcNow();
        if (device.LastSeenAt is null || now - device.LastSeenAt > LastSeenGranularity)
        {
            await db.Devices.Where(d => d.Id == device.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, now), Context.RequestAborted);
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, device.Id.ToString()), new Claim(ClaimTypes.Name, device.Name)], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
