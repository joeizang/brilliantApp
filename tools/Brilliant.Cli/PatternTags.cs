using Brilliant.Core.Content;

namespace Brilliant.Cli;

/// <summary>A problem step's <c>pattern</c>, with where it was found: the lesson's place in its track decides what it may name.</summary>
internal sealed record PatternTag(string File, string Label, Step Step, string TrackDir, int LessonPosition);

/// <summary>
/// The patterns lessons declare and the steps that name them, checked once every lesson is loaded, since a step may name a pattern
/// declared by an earlier lesson or another track. A step can't name a pattern a <em>later</em> lesson of its own track teaches: Practice
/// would then ask the learner to recognise something they haven't met. A prompt that contains its pattern's title gets a warning, as it
/// gives the answer away in Practice; so does a phone alternative's, which is what a phone shows.
/// </summary>
internal sealed class PatternTags
{
    private readonly Dictionary<string, (Pattern Pattern, string LessonId, string TrackDir, int LessonPosition)> _declared = new(StringComparer.Ordinal);
    private readonly List<PatternTag> _tags = [];

    /// <summary>Records a lesson's pattern. A second declaration of the same ID is already reported as a duplicate ID, so the first one wins.</summary>
    public void Declare(Pattern pattern, string lessonId, string trackDir, int lessonPosition) =>
        _declared.TryAdd(pattern.Id, (pattern, lessonId, trackDir, lessonPosition));

    public void Tag(PatternTag tag) => _tags.Add(tag);

    public void Check(ValidationReport report)
    {
        foreach (var tag in _tags)
        {
            var id = tag.Step.Pattern!;
            if (!_declared.TryGetValue(id, out var declared))
            {
                report.Add(tag.File, $"{tag.Label}: pattern '{id}' is not declared in any lesson's 'patterns'.");
                continue;
            }
            if (declared.TrackDir == tag.TrackDir && declared.LessonPosition > tag.LessonPosition)
                report.Add(tag.File, $"{tag.Label}: pattern '{id}' is taught by a later lesson ('{declared.LessonId}'); a step can only name a pattern taught by its own lesson or an earlier one.");
            else
                foreach (var (form, where) in new[] { (tag.Step, ""), (tag.Step.PhoneAlternative, " of the phone alternative") })
                    if (Prompt(form) is { } prompt && OneLine(prompt).Contains(OneLine(declared.Pattern.Title), StringComparison.OrdinalIgnoreCase))
                        report.Warn(tag.File, $"{tag.Label}: the prompt{where} names its own pattern ('{declared.Pattern.Title}'), which gives the answer away in Practice.");
        }
    }

    /// <summary>Prompts are wrapped in the YAML, so a title can be split across two lines of one.</summary>
    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? Prompt(Step? step) => step switch
    {
        WriteCodeStep w => w.Prompt,
        FillBlankStep f => f.Prompt,
        ParsonsStep p => p.Prompt,
        _ => null,
    };
}
