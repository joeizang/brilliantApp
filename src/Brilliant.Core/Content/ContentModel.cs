using System.Text.Json.Serialization;

namespace Brilliant.Core.Content;

/// <summary>Anything in a Content Pack that carries a stable ID. Progress attaches to IDs, never positions.</summary>
public abstract record ContentItem(string Id);

public sealed record Track(string Id, string Title, IReadOnlyList<string> LessonIds) : ContentItem(Id);

/// <summary>An idea a lesson teaches. Mastery and review will be tracked per concept.</summary>
public sealed record Concept(string Id, string Title) : ContentItem(Id);

/// <summary>
/// A problem-solving pattern (prefix sums, a fixed-size window, ...), declared by the one lesson that teaches it. Problem steps name
/// the pattern that solves them (<see cref="Step.Pattern"/>), which is what Practice asks the learner to recognise.
/// </summary>
public sealed record Pattern(string Id, string Title) : ContentItem(Id);

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

/// <summary>How hard a tagged problem is (#128), for choosing what to practise. Authored as <c>difficulty</c>, or the step type's default.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Difficulty>))]
public enum Difficulty { Easy, Medium, Hard }

/// <summary>
/// A question introduced by a lesson that Review will later resurface. It reuses an existing answerable
/// step (<see cref="StepId"/>) of the same lesson as its question, and belongs to one <see cref="ConceptId"/>.
/// </summary>
public sealed record ReviewItem(string Id, string ConceptId, string StepId, ReviewKind Kind = ReviewKind.Concept) : ContentItem(Id);

public sealed record Lesson(string Id, string Title, string TrackId, IReadOnlyList<Step> Steps) : ContentItem(Id)
{
    public IReadOnlyList<Concept> Concepts { get; init; } = [];
    public IReadOnlyList<ReviewItem> ReviewItems { get; init; } = [];

    /// <summary>The patterns this lesson teaches. Each pattern is declared by exactly one lesson.</summary>
    public IReadOnlyList<Pattern> Patterns { get; init; } = [];

    /// <summary>
    /// Practice-only problems: answerable, pattern-tagged steps that live outside the lesson. They never appear in the lesson view and don't
    /// count toward completing it; Practice and Interview draw them once the lesson is completed, alongside the lesson's own tagged steps.
    /// </summary>
    public IReadOnlyList<Step> Practice { get; init; } = [];

    /// <summary>The lesson's steps followed by its practice-only problems: everything progress can be recorded against. A method, not a property, so serialization and pack checks don't walk the steps twice.</summary>
    public IEnumerable<Step> AllSteps() => Steps.Concat(Practice);
}

public sealed record CodeSnippet(string Language, string Code);

/// <summary>One labelled code snippet in a comparison, in any language.</summary>
public sealed record LabelledSnippet(string Label, string Language, string Code);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ExplainStep), "explain")]
[JsonDerivedType(typeof(ChoiceStep), "choice")]
[JsonDerivedType(typeof(PredictOutputStep), "predict-output")]
[JsonDerivedType(typeof(WriteCodeStep), "write-code")]
[JsonDerivedType(typeof(FillBlankStep), "fill-blank")]
[JsonDerivedType(typeof(ParsonsStep), "parsons")]
[JsonDerivedType(typeof(TraceStep), "trace")]
[JsonDerivedType(typeof(LoweredStep), "lowered")]
public abstract record Step(string Id, string Title) : ContentItem(Id)
{
    /// <summary>
    /// Hint ladder for answerable steps, gentlest first (nudge → pattern hint → partial → full walkthrough).
    /// Revealed one rung at a time by the hint ladder; each rung used lowers the rating Review infers.
    /// </summary>
    public IReadOnlyList<string> Hints { get; init; } = [];

    /// <summary>
    /// The reference solution played in the trace player, as the ladder's last rung (the "full solution"). Null when the step has none.
    /// When present, the step has at most three <see cref="Hints"/>: the walkthrough is the fourth rung.
    /// </summary>
    public Walkthrough? Walkthrough { get; init; }

    /// <summary>
    /// The step is awkward to do on a phone (it asks the learner to type a lot of code), so a phone says "best on a larger screen".
    /// Authored as <c>largeScreenPreferred</c>. Has no effect on any other screen.
    /// </summary>
    public bool LargeScreenPreferred { get; init; }

    /// <summary>
    /// A tap-friendly form of the same question (a Parsons or fill-in-the-blank step) for a phone, authored as <c>phoneAlternative</c>
    /// on a large-screen step. It is not a step of the lesson: it has no place in <c>Lesson.Steps</c> and its progress is
    /// the step's own. Doing it completes the step, and what it records (answers, hints, review answers) is recorded against the
    /// step's ID, so a phone and a laptop agree about what was done whichever form was used.
    /// </summary>
    public Step? PhoneAlternative { get; init; }

    /// <summary>
    /// The ID of the <see cref="Content.Pattern"/> that solves this problem, or null when the step isn't about a pattern. Only
    /// write-code, fill-blank and Parsons steps carry one; only tagged steps are offered in Practice.
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// What in the prompt points to <see cref="Pattern"/>, shown once the learner has picked a pattern in Practice or Review, right or
    /// wrong. Every tagged step has one; null when <see cref="Pattern"/> is.
    /// </summary>
    public string? PatternCue { get; init; }

    /// <summary>The authored difficulty of a tagged step, or null to take the default (<see cref="Level"/>).</summary>
    public Difficulty? Difficulty { get; init; }

    /// <summary>
    /// How hard this problem is: its authored <see cref="Difficulty"/>, else fill-in-the-blank and Parsons are easy (the code is mostly
    /// given) and anything else is medium. Only hard needs saying.
    /// </summary>
    [JsonIgnore]
    public Difficulty Level => Difficulty ?? (this is FillBlankStep or ParsonsStep ? Content.Difficulty.Easy : Content.Difficulty.Medium);

    /// <summary>The form of this question to put in front of the learner on a screen of this class: the phone alternative on a phone, else the step itself.</summary>
    public Step For(ScreenClass screen) => screen == ScreenClass.Phone && PhoneAlternative is { } alt ? alt : this;
}

/// <summary>
/// A reference solution to walk through: the <see cref="Code"/> to run and record (the solution plus a call that exercises it),
/// the <see cref="Visuals"/> to draw while it runs (may be none: the variables table still shows), and the markdown
/// <see cref="Explanation"/> of why it works, shown beside the player.
/// </summary>
public sealed record Walkthrough(string Code, IReadOnlyList<Visual> Visuals, string Explanation)
{
    // Missing on existing Python packs; omitted when writing them to preserve their bytes.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; init; }

    /// <summary>
    /// The run recorded when the pack was built, for a language whose runtime can't trace on every device (C#). The player plays it
    /// as it is; nothing runs on the device. Null for Python, which is recorded when the step is shown.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Brilliant.Core.Code.TraceResult? Recorded { get; init; }
}

/// <summary>Text (markdown), optional code snippets and an optional comparison of labelled snippets.</summary>
public sealed record ExplainStep(
    string Id,
    string Title,
    string Body,
    IReadOnlyList<CodeSnippet> Snippets,
    [property: JsonConverter(typeof(ComparisonJsonConverter))]
    IReadOnlyList<LabelledSnippet>? Comparison) : Step(Id, Title);

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
/// result must equal the expression <see cref="Expected"/>. Both are source text in the owning exercise's language, compared using that runtime's equality (structural for C# arrays, records and tuples). Python examples: <c>[3, 1, 2], 2</c> and <c>(1, 3)</c>.
/// <see cref="Compare"/> is null to compare the result itself, or <see cref="CompareLinkedList"/> when the function returns the head of
/// a linked list: the harness then follows <c>.next</c> from it, collects each node's <c>.val</c> into a list, and compares that list
/// (<c>None</c> is the empty list). <see cref="MaxAllocatedBytes"/> (C# only) is the most heap memory one call may allocate; null means the
/// test does not check allocations.
/// </summary>
public sealed record CodeTest(
    string Input,
    string Expected,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Compare = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? MaxAllocatedBytes = null)
{
    public const string CompareLinkedList = "linked_list";
}

/// <summary>
/// "Write the function." The learner edits <see cref="Starter"/> code; on submit it is run against the hidden
/// <see cref="Tests"/> by calling <see cref="Entrypoint"/>. <see cref="Language"/> is declared per exercise; every test uses that language.
/// </summary>
public sealed record WriteCodeStep(
    string Id,
    string Title,
    string Prompt,
    string Language,
    string Starter,
    string Entrypoint,
    IReadOnlyList<CodeTest> Tests) : Step(Id, Title)
{
    /// <summary>
    /// The starter files of a multi-file C# exercise (an interface, an implementation, a consumer), in tab order; null for the usual single
    /// file, whose code is <see cref="Starter"/>. When present <see cref="Starter"/> holds the first file's code, so a reader that doesn't know
    /// about files still shows something sensible.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CodeFile>? Files { get; init; }

    [JsonIgnore]
    public bool IsMultiFile => Files is { Count: > 1 };
}

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

public sealed record PackManifest(string PackId, string Version, int FormatVersion)
{
    /// <summary>The target framework used to author and check C# source; absent from legacy and DSA packs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetFramework { get; init; }

    /// <summary>The pinned C# language version; absent from legacy and DSA packs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LangVersion { get; init; }
}

/// <summary>
/// Which variable of a <see cref="TraceStep"/>'s code to draw and how. <see cref="As"/> is the drawing:
/// <see cref="Array"/> (a list, tuple or deque shown as cells), <see cref="Stack"/> (a list or deque, bottom to top),
/// <see cref="Queue"/> (a list or deque, front to back), <see cref="Dict"/> (key/value rows), <see cref="Set"/> (members),
/// <see cref="LinkedList"/> (nodes joined by arrows, from the node the variable holds), <see cref="Tree"/> (a binary tree, from the
/// root node the variable holds, laid out level by level), <see cref="Heap"/> (a list kept in heap order, shown as cells and as the
/// tree they stand for, the children of cell i at 2i+1 and 2i+2), <see cref="Grid"/> (a list of lists or strings, as a 2-D table), <see cref="Graph"/>
/// (a dict of each node's neighbours, its nodes round a circle) or <see cref="CallStack"/> (the calls under way,
/// one frame each, the running one on top; it draws no variable, so <see cref="Variable"/> is empty).
/// <see cref="Pointers"/> name variables drawn as labelled markers: for an array, stack, queue or heap, int variables, on the cell they
/// index (a heap's on its tree's node too); for a dict or set, variables holding a key, on the row with that key (a lookup); for a linked list or a tree, variables
/// holding a node, on that node (or at None). <see cref="Next"/> and <see cref="Value"/> are a linked list's node attributes: the link
/// to the next node and the value shown (<see cref="Value"/> is a tree node's too). <see cref="Left"/> and <see cref="Right"/> are a
/// tree node's links to its children. <see cref="Visited"/>, for a tree, names a list or set whose members (values, or the nodes
/// themselves) mark the nodes visited, numbered in order for a list. <see cref="Functions"/>, for a call stack, keeps only the calls
/// of those functions (empty: all of them). For a grid, <see cref="Pointers"/> name variables holding a (row, col) pair and
/// <see cref="Cells"/> pairs of int variables, each drawn on the cell they index; <see cref="RowLabels"/> and <see cref="ColLabels"/> name
/// a string, list or tuple labelling the rows and columns, from row or column <see cref="LabelOffset"/> on (so <c>dp[i][j]</c> over
/// <c>a[i - 1]</c> and <c>b[j - 1]</c> takes 1); <see cref="Highlight"/> names a list or set of (row, col) pairs, or a grid of
/// True/False flags, shading the cells it marks. For a graph, <see cref="Pointers"/> name variables holding a node (a value, as the dict's
/// keys are); <see cref="Directed"/> draws its edges as arrows; a dict of neighbours is weighted by its values,
/// and with <see cref="Weights"/> a neighbour given as a (node, weight) pair is that node with that weight; <see cref="Edge"/> names the variables holding the ends of the edge being explored,
/// which is emphasised; <see cref="Visited"/> names a list, set or dict of the nodes visited and <see cref="Frontier"/> a list or deque of
/// those waiting (a queue or a stack), each marking its nodes. <see cref="Max"/> labels a heap a max-heap; it changes nothing else.
/// </summary>
public sealed record Visual(string Variable, string As, IReadOnlyList<string> Pointers,
    string Next = Visual.DefaultNext, string Value = Visual.DefaultValue, IReadOnlyList<string>? Functions = null,
    string Left = Visual.DefaultLeft, string Right = Visual.DefaultRight, string? Visited = null)
{
    public IReadOnlyList<string> Functions { get; init; } = Functions ?? [];

    /// <summary>A grid's pairs of int variables, each drawn on the cell <c>grid[Row][Col]</c>.</summary>
    public IReadOnlyList<CellPair> Cells { get; init => field = value.Count == 0 ? [] : value; } = [];
    public string? RowLabels { get; init; }
    public string? ColLabels { get; init; }
    public int LabelOffset { get; init; }
    public string? Highlight { get; init; }

    /// <summary>A graph's edges have a direction, so each is drawn as an arrow.</summary>
    public bool Directed { get; init; }
    public bool Weights { get; init; }
    public EdgeNames? Edge { get; init; }
    public string? Frontier { get; init; }

    /// <summary>A heap is labelled a max-heap rather than a min-heap; it is only a label.</summary>
    public bool Max { get; init; }

    public const string Array = "array";
    public const string Stack = "stack";
    public const string Queue = "queue";
    public const string Dict = "dict";
    public const string Set = "set";
    public const string LinkedList = "linked_list";
    public const string Tree = "tree";
    public const string Heap = "heap";
    public const string Grid = "grid";
    public const string Graph = "graph";
    public const string CallStack = "call_stack";

    public const string DefaultNext = "next";
    public const string DefaultValue = "val";
    public const string DefaultLeft = "left";
    public const string DefaultRight = "right";

    /// <summary>Every drawing the player knows, in the order the docs list them.</summary>
    public static readonly IReadOnlyList<string> Kinds = [Array, Stack, Queue, Dict, Set, LinkedList, Tree, Heap, Grid, Graph, CallStack];

    /// <summary>The drawings of a sequence as cells, whose pointers are indexes.</summary>
    public static readonly IReadOnlyList<string> Sequences = [Array, Stack, Queue];
}

/// <summary>A grid pointer made of two int variables, the row's and the column's. <see cref="Name"/> is how the trace names it: <c>i,j</c>.</summary>
public sealed record CellPair(string Row, string Col)
{
    public string Name => $"{Row},{Col}";
}

/// <summary>The variables holding the two ends of the graph edge being explored, e.g. <c>node</c> and <c>neighbour</c>.</summary>
public sealed record EdgeNames(string From, string To);

/// <summary>
/// "Watch it run." The learner steps forward and back through the execution of <see cref="Code"/>, seeing the current line,
/// the local variables and each <see cref="Visuals"/> entry at every step. Nothing is answered; the step completes like an explanation.
/// </summary>
public sealed record TraceStep(
    string Id,
    string Title,
    string Body,
    string Code,
    IReadOnlyList<Visual> Visuals) : Step(Id, Title)
{
    // Older packs contain Python traces without a language field.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; init; }

    /// <summary>
    /// The run recorded when the pack was built, for a language whose runtime can't trace on every device (C#). The player plays it
    /// as it is; nothing runs on the device. Null for Python, which is recorded when the step is shown.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Brilliant.Core.Code.TraceResult? Recorded { get; init; }
}

/// <summary>
/// What the compiler turns a C# snippet into, produced when the pack is built (Roslyn compiles it, ICSharpCode.Decompiler reads it back):
/// <see cref="CSharp"/> is the lowered C#, with the compiler's own state machines, enumerators and try/finally blocks written out, and
/// <see cref="Il"/> is the IL. Authors never write either.
/// </summary>
public sealed record LoweredCode(string CSharp, string Il);

/// <summary>
/// "What did the compiler do with this?" The learner reads <see cref="Code"/> beside what it lowers to (C# or IL, their choice) and answers
/// a question about the pair. Typed variant: <see cref="Accepted"/> answers plus authored <see cref="Mistakes"/>. Multiple-choice variant:
/// <see cref="Options"/> is non-empty (exactly one correct) and the typed fields are empty, as for <see cref="PredictOutputStep"/>.
/// Only C# has a lowered form, so there is no language to choose.
/// </summary>
public sealed record LoweredStep(
    string Id,
    string Title,
    string Prompt,
    string Code,
    IReadOnlyList<string> Accepted,
    IReadOnlyList<MistakePattern> Mistakes,
    IReadOnlyList<ChoiceOption> Options) : Step(Id, Title)
{
    [JsonIgnore] public bool IsTyped => Options.Count == 0;

    /// <summary>
    /// The lowered forms of <see cref="Code"/>, recorded when the pack was built. Never authored; null in a pack source that hasn't been
    /// built yet, and the view then has nothing to show beside the code.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LoweredCode? Lowered { get; init; }
}
