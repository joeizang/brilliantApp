using Brilliant.Core.Content;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Practice;

/// <summary>A problem to practise, unlabelled: the step, the pattern that solves it, and the patterns offered to choose from.</summary>
/// <param name="Choices">The right pattern and up to three other learned patterns, in random order.</param>
public sealed record PracticeProblem(PatternStep Problem, Pattern Pattern, IReadOnlyList<Pattern> Choices)
{
    public Lesson Lesson => Problem.Lesson.Lesson;
    public Step Step => Problem.Step;
}

/// <summary>Whether Practice can start: it needs <see cref="PracticeSelector.MinimumPatterns"/> learned patterns and a problem to offer.</summary>
/// <param name="PatternsLearned">Patterns whose teaching lesson is completed.</param>
/// <param name="Problems">Tagged problems in completed lessons whose pattern is learned.</param>
public sealed record PracticeAvailability(int PatternsLearned, int Problems)
{
    public bool IsAvailable => PatternsLearned >= PracticeSelector.MinimumPatterns && Problems > 0;
}

/// <summary>
/// Chooses what Practice offers next. Pure: the same state, previous problem and random sequence always give the same problem.
/// <para>
/// Eligible problems are the tagged problem steps of completed lessons whose pattern is learned (its own lesson completed, so the right
/// answer is always one the learner has been taught). They fall into tiers, weakest first:
/// </para>
/// <list type="number">
/// <item>needs work: its pick or re-solve was last rated Again, or its re-solve is due;</item>
/// <item>never practised;</item>
/// <item>the rest.</item>
/// </list>
/// Within a tier the problems of the lowest pattern mastery come first, then the least recently practised, then content order. One is
/// picked at random from the first <see cref="Band"/> of the weakest non-empty tier, so a visit varies but never strays from what is
/// weakest. The problem just offered is not offered again straight away, unless it is the only one.
/// </summary>
public static class PracticeSelector
{
    /// <summary>With fewer learned patterns there is nothing meaningful to choose between.</summary>
    public const int MinimumPatterns = 3;

    /// <summary>How many patterns a pick offers, the right one included.</summary>
    public const int ChoiceCount = 4;

    /// <summary>How many of the weakest problems the next one is drawn from.</summary>
    public const int Band = 3;

    public static PracticeAvailability Availability(LearnerState state) =>
        new(state.Patterns.Count(p => p.IsLearned), Eligible(state).Count);

    /// <summary>The next problem, or null when Practice isn't available.</summary>
    /// <param name="previousStepId">The step offered last in this visit, never offered twice in a row.</param>
    public static PracticeProblem? Next(LearnerState state, Random random, string? previousStepId = null)
    {
        if (!Availability(state).IsAvailable) return null;
        var eligible = Eligible(state);
        if (eligible.Count > 1) eligible = eligible.Where(e => e.Step.Step.Id != previousStepId).ToList();

        var ranked = eligible
            .Select((e, index) => (e.Step, e.Pattern, Index: index, Tier: e.Step.NeedsWork(state.AsOf) ? 0 : e.Step.IsPractised ? 2 : 1))
            .OrderBy(e => e.Tier).ThenBy(e => e.Pattern.Mastery)
            .ThenBy(e => e.Step.LastPractised ?? DateTimeOffset.MinValue).ThenBy(e => e.Index)
            .ToList();
        var band = ranked.Where(e => e.Tier == ranked[0].Tier).Take(Band).ToList();
        var chosen = band[random.Next(band.Count)];
        return new PracticeProblem(chosen.Step, chosen.Pattern.Pattern, Choices(state, chosen.Pattern.Pattern, random));
    }

    /// <summary>
    /// The patterns to choose from for a problem solved by <paramref name="right"/>: it and <see cref="ChoiceCount"/> − 1 other learned
    /// patterns drawn at random (all of them when fewer are learned), shuffled.
    /// </summary>
    public static IReadOnlyList<Pattern> Choices(LearnerState state, Pattern right, Random random)
    {
        var others = state.Patterns.Where(p => p.IsLearned && p.Pattern.Id != right.Id).Select(p => p.Pattern).ToArray();
        random.Shuffle(others);
        var choices = others.Take(ChoiceCount - 1).Append(right).ToArray();
        random.Shuffle(choices);
        return choices;
    }

    /// <summary>Every problem Practice may offer, with its pattern: by pattern, then content order.</summary>
    private static List<(PatternStep Step, PatternMastery Pattern)> Eligible(LearnerState state) =>
        state.Patterns.Where(p => p.IsLearned).SelectMany(p => p.Steps.Select(s => (s, p))).ToList();
}
