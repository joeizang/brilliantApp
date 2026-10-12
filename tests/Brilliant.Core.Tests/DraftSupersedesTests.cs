using Brilliant.Core.Drafts;

namespace Brilliant.Core.Tests;

public class DraftSupersedesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static CodeDraft D(string? code, int seconds) => new("step.a", code, T0.AddSeconds(seconds));

    [Fact]
    public void Anything_beats_nothing() => Assert.True(D("x", 0).Supersedes(null));

    [Fact]
    public void The_newer_timestamp_wins_whatever_the_code()
    {
        Assert.True(D("a", 1).Supersedes(D("zzz", 0)));
        Assert.False(D("zzz", 0).Supersedes(D("a", 1)));
    }

    [Fact]
    public void A_reset_that_is_newer_beats_an_edit_and_an_edit_that_is_newer_beats_a_reset()
    {
        Assert.True(D(null, 5).Supersedes(D("work", 1)));
        Assert.True(D("work", 6).Supersedes(D(null, 5)));
    }

    [Fact]
    public void The_same_instant_in_a_different_offset_is_a_tie_not_a_difference()
    {
        var elsewhere = new CodeDraft("step.a", "x", T0.ToOffset(TimeSpan.FromHours(3)));
        Assert.False(elsewhere.Supersedes(D("x", 0)));
        Assert.False(D("x", 0).Supersedes(elsewhere));
    }

    [Fact]
    public void An_identical_draft_does_not_supersede_itself() => Assert.False(D("x", 0).Supersedes(D("x", 0)));

    [Fact]
    public void A_tie_is_broken_the_same_way_from_either_side()
    {
        var (a, b) = (D("apple", 0), D("banana", 0));
        Assert.True(b.Supersedes(a));
        Assert.False(a.Supersedes(b));
        Assert.True(a.Supersedes(D(null, 0)));   // a tombstone is the smallest
    }
}
