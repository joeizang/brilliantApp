using System.Text.Json.Serialization;

namespace Brilliant.Core.Content;

/// <summary>Anything in a Content Pack that carries a stable ID. Progress attaches to IDs, never positions.</summary>
public abstract record ContentItem(string Id);

public sealed record Track(string Id, string Title, IReadOnlyList<string> LessonIds) : ContentItem(Id);

/// <summary>An idea a lesson teaches. Mastery and review will be tracked per concept.</summary>
public sealed record Concept(string Id, string Title) : ContentItem(Id);

/// <summary>What a review item trains. Pattern items are weighted up in the daily queue.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewKind>))]
public enum ReviewKind
{
    /// <summary>"What does this do?" A question about an idea. The default.</summary>
    Concept,

    /// <summary>"Which pattern fits this problem?" Authored in a lesson's <c>reviewItems</c> with <c>kind: pattern</c>.</summary>
    Pattern,

    /// <summary>Redo a problem that was answered incorrectly. Never authored: the projector derives it from the event log.</summary>
    Resolve,
}

/// <summary>
/// A question introduced by a lesson that Review will later resurface. It reuses an existing answerable
/// step (<see cref="StepId"/>) of the same lesson as its question, and belongs to one <see cref="ConceptId"/>.
/// </summary>
public sealed record ReviewItem(string Id, string ConceptId, string StepId, ReviewKind Kind = ReviewKind.Concept) : ContentItem(Id);

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
[JsonDerivedType(typeof(WriteCodeStep), "write-code")]
[JsonDerivedType(typeof(FillBlankStep), "fill-blank")]
[JsonDerivedType(typeof(ParsonsStep), "parsons")]
[JsonDerivedType(typeof(TraceStep), "trace")]
public abstract record Step(string Id, string Title) : ContentItem(Id)
{
    /// <summary>
    /// Hint ladder for answerable steps, gentlest first (nudge → pattern hint → partial → full walkthrough).
    /// Revealed one rung at a time by the hint ladder; each rung used lowers the rating Review infers.
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

/// <summary>
/// One hidden test of a write-code step: the learner's function is called as <c>entrypoint(<see cref="Input"/>)</c> and the
/// result must equal the Python expression <see cref="Expected"/>. Both are Python source text, e.g. <c>[3, 1, 2], 2</c> and <c>(1, 3)</c>.
/// </summary>
public sealed record CodeTest(string Input, string Expected);

/// <summary>
/// "Write the function." The learner edits <see cref="Starter"/> code; on submit it is run against the hidden
/// <see cref="Tests"/> by calling <see cref="Entrypoint"/>. <see cref="Language"/> is declared per exercise ("python" for now).
/// </summary>
public sealed record WriteCodeStep(
    string Id,
    string Title,
    string Prompt,
    string Language,
    string Starter,
    string Entrypoint,
    IReadOnlyList<CodeTest> Tests) : Step(Id, Title);

/// <summary>
/// One blank of a fill-in-the-blank step. The learner's text is correct when it equals one of <see cref="Accepted"/> after
/// normalising (see <see cref="AnswerEvaluator.Normalize"/>); <see cref="Mistakes"/> give specific feedback for wrong answers.
/// </summary>
public sealed record Blank(string Id, IReadOnlyList<string> Accepted, IReadOnlyList<MistakePattern> Mistakes);

/// <summary>
/// "Fill in the blanks." Code whose <see cref="Template"/> is read-only except at its <see cref="Blanks"/>, each marked in the
/// template as <c>{{blank-id}}</c> (see <see cref="FillBlankTemplate"/>). Every blank is single-line.
/// </summary>
public sealed record FillBlankStep(
    string Id,
    string Title,
    string Prompt,
    string Language,
    string Template,
    IReadOnlyList<Blank> Blanks) : Step(Id, Title);

/// <summary>One line of a Parsons solution: its code without indentation, and how many levels it is indented.</summary>
public sealed record ParsonsLine(string Text, int Level);

/// <summary>
/// A Parsons problem: the learner is shown the <see cref="Lines"/> of a program shuffled and must put them back in order
/// with the right indentation. <see cref="Lines"/> is the solution, top to bottom.
/// </summary>
public sealed record ParsonsStep(
    string Id,
    string Title,
    string Prompt,
    string Language,
    IReadOnlyList<ParsonsLine> Lines) : Step(Id, Title);

public sealed record PackManifest(string PackId, string Version, int FormatVersion);

/// <summary>
/// Which variable of a <see cref="TraceStep"/>'s code to draw and how. <see cref="As"/> is the drawing, <see cref="Array"/> for now:
/// a list shown as cells. <see cref="Pointers"/> name int variables whose value is drawn as a labelled marker on the cell it indexes.
/// </summary>
public sealed record Visual(string Variable, string As, IReadOnlyList<string> Pointers)
{
    public const string Array = "array";
}

/// <summary>
/// "Watch it run." The learner steps forward and back through the execution of <see cref="Code"/>, seeing the current line,
/// the local variables and each <see cref="Visuals"/> entry at every step. Nothing is answered; the step completes like an explanation.
/// </summary>
public sealed record TraceStep(
    string Id,
    string Title,
    string Body,
    string Code,
    IReadOnlyList<Visual> Visuals) : Step(Id, Title);
