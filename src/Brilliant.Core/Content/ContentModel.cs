using System.Text.Json.Serialization;

namespace Brilliant.Core.Content;

/// <summary>Anything in a Content Pack that carries a stable ID. Progress attaches to IDs, never positions.</summary>
public abstract record ContentItem(string Id);

public sealed record Track(string Id, string Title, IReadOnlyList<string> LessonIds) : ContentItem(Id);

/// <summary>An idea a lesson teaches. Mastery and review will be tracked per concept.</summary>
public sealed record Concept(string Id, string Title) : ContentItem(Id);

/// <summary>
/// A question introduced by a lesson that Review will later resurface. It reuses an existing answerable
/// step (<see cref="StepId"/>) of the same lesson as its question, and belongs to one <see cref="ConceptId"/>.
/// </summary>
public sealed record ReviewItem(string Id, string ConceptId, string StepId) : ContentItem(Id);

public sealed record Lesson(string Id, string Title, string TrackId, IReadOnlyList<Step> Steps) : ContentItem(Id)
{
    public IReadOnlyList<Concept> Concepts { get; init; } = [];
    public IReadOnlyList<ReviewItem> ReviewItems { get; init; } = [];
}

public sealed record CodeSnippet(string Language, string Code);

public sealed record Comparison(string CSharp, string Python);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ExplainStep), "explain")]
[JsonDerivedType(typeof(ChoiceStep), "choice")]
[JsonDerivedType(typeof(PredictOutputStep), "predict-output")]
public abstract record Step(string Id, string Title) : ContentItem(Id)
{
    /// <summary>
    /// Hint ladder for answerable steps, gentlest first (nudge → pattern hint → partial → full walkthrough).
    /// Authored now; shown to the learner by a later issue.
    /// </summary>
    public IReadOnlyList<string> Hints { get; init; } = [];
}

/// <summary>Text (markdown), optional code snippets and an optional side-by-side C#↔Python comparison.</summary>
public sealed record ExplainStep(
    string Id,
    string Title,
    string Body,
    IReadOnlyList<CodeSnippet> Snippets,
    Comparison? Comparison) : Step(Id, Title);

/// <summary>One answer option. <see cref="Feedback"/> explains why it is right or wrong.</summary>
public sealed record ChoiceOption(string Text, bool Correct, string? Feedback);

/// <summary>Multiple-choice (exactly one correct option) or multi-select (<see cref="MultiSelect"/>, one or more).</summary>
public sealed record ChoiceStep(
    string Id,
    string Title,
    string Prompt,
    bool MultiSelect,
    IReadOnlyList<ChoiceOption> Options) : Step(Id, Title);

/// <summary>
/// A typed answer that earns specific feedback. It matches when the normalised response equals one of
/// <see cref="Answers"/> (also normalised) or satisfies <see cref="Regex"/>.
/// </summary>
public sealed record MistakePattern(IReadOnlyList<string> Answers, string? Regex, string Feedback);

/// <summary>
/// "What does this print?" Typed variant: <see cref="Accepted"/> answers plus authored <see cref="Mistakes"/>.
/// Multiple-choice variant: <see cref="Options"/> is non-empty (exactly one correct) and the typed fields are empty.
/// </summary>
public sealed record PredictOutputStep(
    string Id,
    string Title,
    string Prompt,
    CodeSnippet Code,
    IReadOnlyList<string> Accepted,
    IReadOnlyList<MistakePattern> Mistakes,
    IReadOnlyList<ChoiceOption> Options) : Step(Id, Title)
{
    [JsonIgnore] public bool IsTyped => Options.Count == 0;
}

public sealed record PackManifest(string PackId, string Version, int FormatVersion);
