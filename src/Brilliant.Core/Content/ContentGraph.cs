namespace Brilliant.Core.Content;

/// <summary>Immutable, indexed view of a Content Pack. Look items up by stable ID.</summary>
public sealed class ContentGraph
{
    private readonly Dictionary<string, ContentItem> _byId = new(StringComparer.Ordinal);

    public ContentGraph(PackManifest manifest, IEnumerable<Track> tracks, IEnumerable<Lesson> lessons)
    {
        Manifest = manifest;
        Tracks = tracks.ToList();
        Lessons = lessons.ToList();

        foreach (var item in Tracks.Cast<ContentItem>()
                     .Concat(Lessons)
                     .Concat(Lessons.SelectMany(l => l.Steps)))
        {
            if (!_byId.TryAdd(item.Id, item))
                throw new ContentPackException($"Duplicate content ID '{item.Id}'.");
        }
    }

    public PackManifest Manifest { get; }
    public IReadOnlyList<Track> Tracks { get; }
    public IReadOnlyList<Lesson> Lessons { get; }

    public bool TryGet<T>(string id, out T item) where T : ContentItem
    {
        if (_byId.TryGetValue(id, out var found) && found is T typed)
        {
            item = typed;
            return true;
        }
        item = null!;
        return false;
    }

    public T Get<T>(string id) where T : ContentItem =>
        TryGet<T>(id, out var item)
            ? item
            : throw new KeyNotFoundException($"No {typeof(T).Name} with ID '{id}'.");
}

public sealed class ContentPackException(string message, Exception? inner = null) : Exception(message, inner);
