using Brilliant.Core.Content;
using Bunit;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The review-code step (#164): flag lines, name a category, submit, and see what was found, missed and wrongly flagged.</summary>
public sealed class ReviewCodeStepViewTests : ShortcutContext
{
    private static readonly ReviewCodeStep Cache = new(
        "step.review.cache", "Review the cache", "Review this change.", "csharp",
        string.Join('\n',
            "public sealed class Cache",
            "{",
            "    private readonly Dictionary<string, string> _items = new();",
            "    public string Get(string key)",
            "    {",
            "        var found = _items.ContainsKey(key);",
            "        if (found = true) return _items[key];",
            "        return Load(key);",
            "    }",
            "}"),
        [
            new(3, 3, ReviewCategory.Concurrency, "A plain Dictionary is not safe for concurrent readers and writers."),
            new(7, 7, ReviewCategory.Bug, "`=` assigns; this is always true."),
        ],
        [new(8, "Calling Load on a miss is the point of a cache.")]);

    private IRenderedComponent<ReviewCodeStepView> Show(ReviewCodeStep? step = null, Action<ReviewCodeStepView.Attempt>? answered = null, Action? continued = null) =>
        Render<ReviewCodeStepView>(p =>
        {
            p.Add(c => c.Step, step ?? Cache);
            if (answered is not null) p.Add(c => c.OnAnswered, answered);
            if (continued is not null) p.Add(c => c.OnContinue, continued);
        });

    private static void Flag(IRenderedComponent<ReviewCodeStepView> cut, int line, string category)
    {
        cut.Find($".code-line[data-line='{line}'] button.review-line").Click();
        cut.Find($".code-line[data-line='{line}'] button[data-category='{category}']").Click();
    }

    private static void Submit(IRenderedComponent<ReviewCodeStepView> cut) => cut.Find("button.primary").Click();

    [Fact]
    public void It_shows_every_line_numbered_with_nothing_flagged()
    {
        var cut = Show();

        Assert.Equal(10, cut.FindAll(".code-line").Count);
        Assert.Equal("7", cut.Find(".code-line[data-line='7'] .line-number").TextContent);
        Assert.Contains("found = true", cut.Find(".code-line[data-line='7'] code").TextContent);
        Assert.Contains("No lines flagged yet", cut.Find(".flag-count").TextContent);
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
    }

    [Fact]
    public void Tapping_a_line_offers_the_five_kinds_of_problem()
    {
        var cut = Show();

        cut.Find(".code-line[data-line='7'] button.review-line").Click();

        Assert.Equal(["bug", "performance", "concurrency", "api", "security"], cut.FindAll(".code-line[data-line='7'] .picker button[data-category]").Select(b => b.GetAttribute("data-category")));
        Assert.Empty(cut.FindAll(".code-line[data-line='3'] .picker"));
    }

    [Fact]
    public void Choosing_a_kind_flags_the_line_and_closes_the_picker()
    {
        var cut = Show();

        Flag(cut, 7, ReviewCategory.Bug);

        Assert.Equal("Bug", cut.Find(".code-line[data-line='7'] .flag-badge").TextContent);
        Assert.Equal("true", cut.Find(".code-line[data-line='7'] button.review-line").GetAttribute("aria-pressed"));
        Assert.Contains("1 line flagged", cut.Find(".flag-count").TextContent);
        Assert.Empty(cut.FindAll(".picker"));
        Assert.False(cut.Find("button.primary").HasAttribute("disabled"));
    }

    [Fact]
    public void A_flag_can_be_changed_or_removed()
    {
        var cut = Show();
        Flag(cut, 7, ReviewCategory.Performance);

        Flag(cut, 7, ReviewCategory.Bug);
        Assert.Equal("Bug", cut.Find(".code-line[data-line='7'] .flag-badge").TextContent);

        cut.Find(".code-line[data-line='7'] button.review-line").Click();
        cut.Find(".code-line[data-line='7'] button.remove-flag").Click();
        Assert.Empty(cut.FindAll(".flag-badge"));
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
    }

    [Fact]
    public void Tapping_the_open_line_again_closes_the_picker()
    {
        var cut = Show();

        cut.Find(".code-line[data-line='7'] button.review-line").Click();
        cut.Find(".code-line[data-line='7'] button.review-line").Click();

        Assert.Empty(cut.FindAll(".picker"));
    }

    [Fact]
    public void A_perfect_review_is_reported_and_recorded()
    {
        ReviewCodeStepView.Attempt? seen = null;
        var cut = Show(answered: a => seen = a);
        Flag(cut, 3, ReviewCategory.Concurrency);
        Flag(cut, 7, ReviewCategory.Bug);

        Submit(cut);

        Assert.Contains("found 2 of 2", cut.Find(".verdict").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("correct", cut.Find(".verdict").ClassName);
        Assert.True(seen!.Correct);
        Assert.Equal(1.0, seen.Score);
        Assert.Equal([new ReviewFlag(3, "concurrency"), new ReviewFlag(7, "bug")], seen.Flags);
        Assert.Equal(["found", "found"], cut.FindAll(".defect").Select(d => d.GetAttribute("data-outcome")));
    }

    [Fact]
    public void Missed_defects_are_revealed_with_their_explanations_and_the_line_marked()
    {
        var cut = Show();
        Flag(cut, 7, ReviewCategory.Bug);

        Submit(cut);

        var missed = cut.Find(".defect[data-outcome='missed']");
        Assert.Contains("not safe for concurrent", missed.TextContent);
        Assert.Equal("missed", cut.Find(".code-line[data-line='3']").GetAttribute("data-state"));
        Assert.Equal("found", cut.Find(".code-line[data-line='7']").GetAttribute("data-state"));
    }

    [Fact]
    public void The_right_line_under_the_wrong_kind_earns_partial_credit_and_says_so()
    {
        ReviewCodeStepView.Attempt? seen = null;
        var cut = Show(answered: a => seen = a);
        Flag(cut, 3, ReviewCategory.Security);
        Flag(cut, 7, ReviewCategory.Bug);

        Submit(cut);

        Assert.False(seen!.Correct);
        Assert.Equal(0.75, seen.Score);
        Assert.Contains("half credit", cut.Find(".defect[data-outcome='wrong-category']").TextContent);
        Assert.Contains("75%", cut.Find(".score").TextContent);
    }

    [Fact]
    public void A_flag_on_a_fine_line_is_explained_and_struck_through()
    {
        var cut = Show();
        Flag(cut, 3, ReviewCategory.Concurrency);
        Flag(cut, 7, ReviewCategory.Bug);
        Flag(cut, 8, ReviewCategory.Performance);

        Submit(cut);

        Assert.Contains("point of a cache", cut.Find(".false-flag[data-line='8']").TextContent);
        Assert.Equal("false-flag", cut.Find(".code-line[data-line='8']").GetAttribute("data-state"));
        Assert.True(cut.Find(".code-line[data-line='8'] button.review-line").HasAttribute("disabled"));
    }

    [Fact]
    public void After_submitting_the_review_is_locked_and_continue_moves_on()
    {
        var continued = false;
        var answers = 0;
        var cut = Show(answered: _ => answers++, continued: () => continued = true);
        Flag(cut, 7, ReviewCategory.Bug);
        Submit(cut);

        Assert.Empty(cut.FindAll(".picker"));
        cut.Find("button.primary").Click();

        Assert.True(continued);
        Assert.Equal(1, answers);
    }

    [Fact]
    public async Task The_keyboard_shortcut_submits_and_then_continues()
    {
        var continued = false;
        var cut = Show(continued: () => continued = true);
        Flag(cut, 7, ReviewCategory.Bug);

        await Press(StepCommand.Advance);
        Assert.NotEmpty(cut.FindAll(".results"));
        await Press(StepCommand.Advance);

        Assert.True(continued);
    }

    [Fact]
    public void A_diff_colours_additions_and_removals()
    {
        var diff = Cache with { Language = "diff", Code = "-old\n+new\n context", Defects = [new(2, 2, ReviewCategory.Bug, "x")], Decoys = [] };

        var cut = Show(diff);

        Assert.Contains("text-danger", cut.Find(".code-line[data-line='1'] button").ClassName);
        Assert.Contains("text-success", cut.Find(".code-line[data-line='2'] button").ClassName);
        Assert.DoesNotContain("text-", cut.Find(".code-line[data-line='3'] button").ClassName.Replace("text-inherit", "").Replace("text-left", "").Replace("text-[.9rem]", "") ?? "");
    }

    [Fact]
    public void A_different_step_starts_with_a_clean_slate()
    {
        var cut = Show();
        Flag(cut, 7, ReviewCategory.Bug);

        cut.Render(p => p.Add(c => c.Step, Cache with { Id = "step.review.other" }));

        Assert.Empty(cut.FindAll(".flag-badge"));
    }

    [Fact]
    public void Each_line_button_names_its_line_and_flag_for_screen_readers()
    {
        var cut = Show();
        Flag(cut, 7, ReviewCategory.Bug);

        Assert.StartsWith("Line 7: if (found = true)", cut.Find(".code-line[data-line='7'] button.review-line").GetAttribute("aria-label")!);
        Assert.EndsWith("flagged as Bug", cut.Find(".code-line[data-line='7'] button.review-line").GetAttribute("aria-label"));
    }
}
