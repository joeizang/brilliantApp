using System.Text.Json.Serialization;

namespace Brilliant.Core.Content;

/// <summary>Anything in a Content Pack that carries a stable ID. Progress attaches to IDs, never positions.</summary>
public abstract record ContentItem(string Id);

public sealed record Track(string Id, string Title, IReadOnlyList<string> LessonIds) : ContentItem(Id);

public sealed record Lesson(string Id, string Title, string TrackId, IReadOnlyList<Step> Steps) : ContentItem(Id);

public sealed record CodeSnippet(string Language, string Code);

public sealed record Comparison(string CSharp, string Python);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ExplainStep), "explain")]
public abstract record Step(string Id, string Title) : ContentItem(Id);

/// <summary>Text (markdown), optional code snippets and an optional side-by-side C#↔Python comparison.</summary>
public sealed record ExplainStep(
    string Id,
    string Title,
    string Body,
    IReadOnlyList<CodeSnippet> Snippets,
    Comparison? Comparison) : Step(Id, Title);

public sealed record PackManifest(string PackId, string Version, int FormatVersion);
