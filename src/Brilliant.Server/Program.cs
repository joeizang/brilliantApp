using System.Threading.RateLimiting;
using Brilliant.Server;
using Brilliant.Server.Data;
using Brilliant.Server.Pairing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ServerDb>(o => o.UseNpgsql(
    builder.Configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not set.")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<PairingOptions>(builder.Configuration.GetSection("Pairing"));
builder.Services.AddScoped<PairingCodes>();

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
    if (args is ["pairing-code"])
    {
        var (code, _) = await scope.ServiceProvider.GetRequiredService<PairingCodes>().MintAsync();
        Console.WriteLine($"Pairing code: {code}  (valid for {scope.ServiceProvider.GetRequiredService<IOptions<PairingOptions>>().Value.CodeLifetimeMinutes} minutes)");
        return;
    }

    // The very first start has no device to mint a code, so print one.
    if (!await db.Devices.AnyAsync(d => d.RevokedAt == null))
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
