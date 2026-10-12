using System.Security.Cryptography;

namespace Brilliant.Core.Content;

/// <summary>
/// Content Packs downloaded from the household server, kept on the device so they work offline. A file is
/// <c>{packId}_{version}.zip</c>; only packs that load and carry the ID and version their name claims are ever used, and anything
/// that doesn't is deleted. Nothing here can stop the app starting: a cache that is missing, unreadable or damaged is just empty.
/// </summary>
public sealed class PackCache(string directory)
{
    private const string Extension = ".zip";

    /// <summary>Newer versions than this are kept besides the newest, so a pack that turns out bad has something to fall back to.</summary>
    public const int Kept = 2;

    /// <summary>
    /// Checks <paramref name="bytes"/> (that it is a loadable pack of format <see cref="ContentPackFormat.CurrentFormatVersion"/> with this ID
    /// and version) and stores it. Returns the loaded graph; throws <see cref="ContentPackException"/> if the pack is unusable.
    /// </summary>
    public ContentGraph Store(string packId, PackVersion version, byte[] bytes)
    {
        var graph = ContentPackFormat.Load(new MemoryStream(bytes));
        if (graph.Manifest.PackId != packId || !PackVersion.TryParse(graph.Manifest.Version, out var inside) || inside != version)
            throw new ContentPackException($"The pack says it is {graph.Manifest.PackId} {graph.Manifest.Version}, not {packId} {version}.");
        if (!PackIds.IsValid(packId)) throw new ContentPackException("The pack ID is not usable.");

        Directory.CreateDirectory(directory);
        var path = PathFor(packId, version);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Prune(packId);
        return graph;
    }

    /// <summary>The newest cached version of <paramref name="packId"/> that is newer than <paramref name="above"/> and loads, or null.</summary>
    public (ContentGraph Graph, PackVersion Version)? LoadNewest(string packId, PackVersion above)
    {
        foreach (var (version, path) in Versions(packId).OrderByDescending(v => v.Version))
        {
            if (version <= above) break;
            try
            {
                using var stream = File.OpenRead(path);
                var graph = ContentPackFormat.Load(stream);
                if (graph.Manifest.PackId == packId && PackVersion.TryParse(graph.Manifest.Version, out var inside) && inside == version)
                    return (graph, version);
            }
            catch (Exception e) when (e is ContentPackException or IOException or UnauthorizedAccessException) { }
            Delete(path);   // damaged, or not what its name says: it would only be tried again at every start
        }
        return null;
    }

    /// <summary>The newest version of <paramref name="packId"/> held here, whether or not it loads.</summary>
    public PackVersion? Newest(string packId) => Versions(packId).Select(v => (PackVersion?)v.Version).Max();

    private void Prune(string packId)
    {
        foreach (var (_, path) in Versions(packId).OrderByDescending(v => v.Version).Skip(Kept)) Delete(path);
    }

    private IEnumerable<(PackVersion Version, string Path)> Versions(string packId)
    {
        if (!PackIds.IsValid(packId) || !Directory.Exists(directory)) yield break;
        string[] files;
        try { files = Directory.GetFiles(directory, packId + "_*" + Extension); }
        catch (IOException) { yield break; }
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length > packId.Length + 1 && name.StartsWith(packId + "_", StringComparison.Ordinal)
                && PackVersion.TryParse(name[(packId.Length + 1)..], out var version))
                yield return (version, file);
        }
    }

    private string PathFor(string packId, PackVersion version) => Path.Combine(directory, $"{packId}_{version}{Extension}");

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class PackStartup
{
    /// <summary>
    /// The pack the app runs: the bundled one, unless the cache holds a newer, loadable version of the same pack. Whatever goes
    /// wrong with the cache, the bundled pack is what the app starts with. <paramref name="bundled"/> must open a fresh stream each call.
    /// </summary>
    public static ContentGraph Choose(Func<Stream> bundled, PackCache cache)
    {
        ContentGraph graph;
        using (var stream = bundled()) graph = ContentPackFormat.Load(stream);
        try
        {
            if (PackVersion.TryParse(graph.Manifest.Version, out var running) && cache.LoadNewest(graph.Manifest.PackId, running) is { } newer)
                return newer.Graph;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { /* the bundled pack always works */ }
        return graph;
    }
}
