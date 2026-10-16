using System.Security.Claims;

namespace Brilliant.Server.Pairing;

/// <summary>App scope asserted by the token handler after checking the paired device and request header agree.</summary>
internal static class AuthenticatedApp
{
    public const string Claim = "brilliant.appId";

    public static string AppId(this ClaimsPrincipal user) => user.FindFirstValue(Claim)
        ?? throw new InvalidOperationException("An authenticated app scope is required.");
}
