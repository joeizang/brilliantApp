namespace Brilliant.Core.Sync;

/// <summary>The app identity carried on every pairing, sync and pack request.</summary>
public static class AppSync
{
    public const string AppIdHeader = "X-Brilliant-App-Id";

    internal static void RequireCredentials(DeviceCredentials credentials, string appId)
    {
        if (!string.Equals(credentials.AppId, appId, StringComparison.Ordinal))
            throw new SyncException(SyncFailure.Unauthorized, "These credentials belong to a different app. Pair this app with the server.");
    }
}
