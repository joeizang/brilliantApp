namespace Brilliant.Core.Apps;

/// <summary>Local app isolation, retaining the installed DSA app's existing paths and key.</summary>
public sealed class AppStoragePaths(AppManifest manifest, string platformDataDirectory)
{
    public const string DsaAppId = "com.joeizang.brilliantapp";

    public string DataDirectory { get; } = manifest.AppId == DsaAppId
        ? platformDataDirectory : Path.Combine(platformDataDirectory, "apps", manifest.AppId);

    public string CredentialsKey { get; } = manifest.AppId == DsaAppId
        ? "brilliant.device-credentials" : $"{manifest.AppId}.device-credentials";
}
