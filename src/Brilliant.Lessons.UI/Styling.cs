using Brilliant.Core.Progress;

namespace Brilliant.Lessons.UI;

/// <summary>
/// Tailwind classes for elements whose look depends on state and that more than one component draws. The state words
/// ("selected", "right", "wrong") stay in the class list as hooks for tests and for selecting in the browser.
/// </summary>
internal static class Styling
{
    /// <summary>An answer option (choice and multiple-choice predict-output). The ring is a shadow, so selecting never shifts the layout.</summary>
    public static string Option(OptionState state) => state switch
    {
        OptionState.Selected => "option selected option-card bg-surface-raised ring-2 ring-accent",
        OptionState.Right => "option right option-card bg-success-soft ring-2 ring-success",
        OptionState.Wrong => "option wrong option-card bg-danger-soft ring-2 ring-danger",
        _ => "option option-card bg-surface-raised hover:ring-1 hover:ring-border",
    };

    /// <summary>A card button that rises on hover and settles on press (a track on Tracks, Review on Today and Tracks).</summary>
    private const string Lift = "w-full cursor-pointer rounded-lg border font-inherit text-left text-inherit transition-[translate,scale,box-shadow] duration-250 ease-app hover:-translate-y-[.125rem] hover:shadow-(--shadow-lift) active:translate-y-0 active:scale-[.995]";

    /// <summary>A track's card on the Tracks screen.</summary>
    public const string TrackCard = "card grid gap-2 border-hairline bg-surface-raised px-6 py-5 shadow-(--shadow-card) " + Lift;

    /// <summary>The "Review, N due" button, which draws the eye a little more than a track card.</summary>
    public const string ReviewCard = "review-card flex items-center justify-between gap-4 border-accent/45 bg-accent-wash px-6 py-4 " + Lift;

    /// <summary>The colours of the "Known / Learning / Seen" pill beside a concept's name.</summary>
    public static string LevelPill(ConceptLevel level) => level switch
    {
        ConceptLevel.Known => "bg-success-tint text-success",
        ConceptLevel.Learning => "bg-accent-tint text-accent",
        _ => "bg-surface text-fg-secondary",
    };
}

internal enum OptionState { Idle, Selected, Right, Wrong }
