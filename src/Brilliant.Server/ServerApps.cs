using Brilliant.Core.Apps;

namespace Brilliant.Server;

/// <summary>The app IDs this server serves, parsed by the same manifest contract as the native host and authoring CLI.</summary>
public sealed class ServerApps
{
    private readonly IReadOnlyDictionary<string, AppManifest> _apps;

    private ServerApps(IReadOnlyDictionary<string, AppManifest> apps) => _apps = apps;

    public bool Contains(string? appId) => appId is not null && _apps.ContainsKey(appId);

    public static ServerApps Load(string directory)
    {
        var apps = new Dictionary<string, AppManifest>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "manifest.yaml", SearchOption.AllDirectories))
        {
            var manifest = AppManifest.Parse(File.ReadAllText(path));
            if (!apps.TryAdd(manifest.AppId, manifest))
                throw new AppManifestException($"Duplicate server app ID '{manifest.AppId}' in '{path}'.");
        }
        if (apps.Count == 0) throw new AppManifestException($"No app manifests were found in '{directory}'.");
        return new ServerApps(apps);
    }
}
