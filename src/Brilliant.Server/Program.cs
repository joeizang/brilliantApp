using System.Threading.RateLimiting;
using Brilliant.Core.Apps;
using Brilliant.Server;
using Brilliant.Server.Data;
using Brilliant.Server.Pairing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Behind docker compose the server speaks HTTPS only, with the household's certificate (docs/https.md). Without the settings it keeps
// the default plain endpoint, for `dotnet run` and the tests.
var tls = ServerTls.From(builder.Configuration);
if (tls is not null)
{
    var certificate = ServerTls.Load(tls, TimeProvider.System);
    if (ServerTls.ExpiresSoon(certificate, TimeProvider.System))
        Console.Error.WriteLine($"warn: the TLS certificate expires on {certificate.NotAfter:yyyy-MM-dd}. Run scripts/local-ca.sh to renew it (docs/https.md).");
    builder.WebHost.ConfigureKestrel(o => ServerTls.Listen(o, certificate, tls.Port));
}

builder.Services.AddDbContext<ServerDb>(o => o.UseNpgsql(
    builder.Configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not set.")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(ServerApps.Load(builder.Configuration["Apps:Directory"] ?? Path.Combine(AppContext.BaseDirectory, "apps")));
builder.Services.Configure<PairingOptions>(builder.Configuration.GetSection("Pairing"));
builder.Services.AddScoped<PairingCodes>();
builder.Services.AddFeedbackProxy(builder.Configuration);

builder.Services.AddAuthentication(DeviceTokenDefaults.Scheme).AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(DeviceTokenDefaults.Scheme, null);
builder.Services.AddAuthorization();

// Pairing codes are short, so guessing is capped per client address.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(Endpoints.PairingPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IOptions<PairingOptions>>().Value.AttemptsPerMinute,
            Window = TimeSpan.FromMinutes(1),
        }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ServerDb>();
    await db.Database.MigrateAsync();

    // `dotnet Brilliant.Server.dll pairing-code` prints a fresh code and exits (see docs/server.md).
    if (args is ["pairing-code"] or ["pairing-code", "--app", _])
    {
        var appId = args.Length == 3 ? args[2] : AppStoragePaths.DsaAppId;
        var (code, _) = await scope.ServiceProvider.GetRequiredService<PairingCodes>().MintAsync(appId);
        Console.WriteLine($"Pairing code for {appId}: {code}  (valid for {scope.ServiceProvider.GetRequiredService<IOptions<PairingOptions>>().Value.CodeLifetimeMinutes} minutes)");
        return;
    }

    // The very first start has no device to mint a code, so print one.
    if (scope.ServiceProvider.GetRequiredService<ServerApps>().Contains(AppStoragePaths.DsaAppId) &&
        !await db.Devices.AnyAsync(d => d.AppId == AppStoragePaths.DsaAppId && d.RevokedAt == null))
    {
        var (code, _) = await scope.ServiceProvider.GetRequiredService<PairingCodes>().MintAsync();
        app.Logger.LogWarning("No paired devices. Pairing code: {Code} (valid for {Minutes} minutes).", code,
            scope.ServiceProvider.GetRequiredService<IOptions<PairingOptions>>().Value.CodeLifetimeMinutes);
    }
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapServerEndpoints();

app.Run();

/// <summary>Public so the integration tests can host the server in-process.</summary>
public partial class Program;
