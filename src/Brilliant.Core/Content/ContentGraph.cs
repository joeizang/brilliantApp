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
                     .Concat(Lessons.SelectMany(l => l.Steps))
                     .Concat(Lessons.SelectMany(l => l.Concepts))
                     .Concat(Lessons.SelectMany(l => l.ReviewItems))
                     .Concat(Lessons.SelectMany(l => l.Patterns)))
        {
            if (!_byId.TryAdd(item.Id, item))
                throw new ContentPackException($"Duplicate content ID '{item.Id}'.");
        }
    }

    public PackManifest Manifest { get; }

    /// <summary>Every ID in the pack (tracks, lessons, steps, concepts, review items, patterns). IDs are what progress is recorded against.</summary>
    public IReadOnlyCollection<string> Ids => _byId.Keys;
    public IReadOnlyList<Track> Tracks { get; }
    public IReadOnlyList<Lesson> Lessons { get; }

    /// <summary>
    /// What in the pack refers to something that isn't there: a track's lessons, a lesson's track, a review item's concept and step, a step's pattern.
    /// A pack with any of these loads as JSON but breaks the screens that follow the reference, so it must not be adopted.
    /// </summary>
    public IReadOnlyList<string> ReferenceProblems()
    {
        var problems = new List<string>();
        foreach (var track in Tracks)
            foreach (var lessonId in track.LessonIds)
                if (!TryGet<Lesson>(lessonId, out _)) problems.Add($"Track '{track.Id}' lists lesson '{lessonId}', which is not in the pack.");
        foreach (var lesson in Lessons)
        {
            if (!TryGet<Track>(lesson.TrackId ?? "", out _)) problems.Add($"Lesson '{lesson.Id}' belongs to track '{lesson.TrackId}', which is not in the pack.");
            foreach (var item in lesson.ReviewItems)
            {
                if (!lesson.Concepts.Any(c => c.Id == item.ConceptId)) problems.Add($"Review item '{item.Id}' is about concept '{item.ConceptId}', which lesson '{lesson.Id}' does not have.");
                if (!lesson.Steps.Any(s => s.Id == item.StepId)) problems.Add($"Review item '{item.Id}' uses step '{item.StepId}', which lesson '{lesson.Id}' does not have.");
            }
            foreach (var step in lesson.Steps)
                if (step.Pattern is { } pattern && !TryGet<Pattern>(pattern, out _))
                    problems.Add($"Step '{step.Id}' is solved by pattern '{pattern}', which is not in the pack.");
        }
        return problems;
    }

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
