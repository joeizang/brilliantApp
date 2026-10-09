using Brilliant.Core.Content;

namespace Brilliant.Core.Tests;

public class AnswerEvaluatorTests
{
    private static PredictOutputStep Typed(string[] accepted, params MistakePattern[] mistakes) =>
        new("step.p", "P", "?", new CodeSnippet("python", "print(1)"), accepted, mistakes, []);

    private static readonly PredictOutputStep Choice = new("step.c", "C", "?", new CodeSnippet("python", "print(1)"), [], [],
        [new("1", true, "Right."), new("2", false, "Off by one.")]);

    [Theory]
    [InlineData("  hello   world  ", "hello world")]
    [InlineData("a\t\tb", "a b")]
    [InlineData("line1  \r\n  line2\r\n", "line1\nline2")]
    [InlineData("\n\nx\n\n", "x")]
    [InlineData("“hi”", "'hi'")]
    [InlineData("\"hi\"", "'hi'")]
    [InlineData("it’s", "it's")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Normalize_makes_equivalent_outputs_comparable(string? input, string expected) =>
        Assert.Equal(expected, AnswerEvaluator.Normalize(input));

    [Fact]
    public void Normalize_keeps_case_and_inner_blank_lines()
    {
        Assert.Equal("A\n\nb", AnswerEvaluator.Normalize("A\n\nb"));
        Assert.NotEqual(AnswerEvaluator.Normalize("Hi"), AnswerEvaluator.Normalize("hi"));
    }

    [Theory]
    [InlineData("3", true)]
    [InlineData("  3 \n", true)]
    [InlineData("three", true)]
    [InlineData("4", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Typed_answer_is_correct_when_it_matches_any_accepted_answer(string? response, bool expected) =>
        Assert.Equal(expected, AnswerEvaluator.Evaluate(Typed(["3", "three"]), response).IsCorrect);

    [Fact]
    public void Quote_style_and_spacing_do_not_matter()
    {
        var step = Typed(["['a', 'b']"]);
        Assert.True(AnswerEvaluator.Evaluate(step, "[\"a\",  \"b\"]").IsCorrect);
        Assert.True(AnswerEvaluator.Evaluate(Typed(["a b"]), "a\tb").IsCorrect);
    }

    [Fact]
    public void Multi_line_output_compares_line_by_line()
    {
        var step = Typed(["1\n2\n3"]);
        Assert.True(AnswerEvaluator.Evaluate(step, "1 \r\n2\r\n  3").IsCorrect);
        Assert.False(AnswerEvaluator.Evaluate(step, "1 2 3").IsCorrect);
    }

    [Fact]
    public void Correct_answer_has_no_feedback_even_if_a_mistake_would_match()
    {
        var step = Typed(["3"], new MistakePattern([], @"^\d$", "digit"));
        var result = AnswerEvaluator.Evaluate(step, "3");
        Assert.True(result.IsCorrect);
        Assert.Null(result.Feedback);
    }

    [Fact]
    public void Exact_mistake_answer_yields_its_feedback()
    {
        var step = Typed(["3"], new MistakePattern(["3.5", "4"], null, "// floors"));
        Assert.Equal("// floors", AnswerEvaluator.Evaluate(step, " 3.5 ").Feedback);
        Assert.Equal("// floors", AnswerEvaluator.Evaluate(step, "4").Feedback);
        Assert.False(AnswerEvaluator.Evaluate(step, "4").IsCorrect);
    }

    [Fact]
    public void Regex_mistake_matches_the_normalised_response()
    {
        var step = Typed(["[0, 1]"], new MistakePattern([], @"^\[ ?\d ?\]$", "one element too few"));
        Assert.Equal("one element too few", AnswerEvaluator.Evaluate(step, "[ 7 ]").Feedback);
        Assert.Null(AnswerEvaluator.Evaluate(step, "[7, 8]").Feedback);
    }

    [Fact]
    public void First_matching_mistake_wins()
    {
        var step = Typed(["x"], new MistakePattern(["a"], null, "first"), new MistakePattern([], "a", "second"));
        Assert.Equal("first", AnswerEvaluator.Evaluate(step, "a").Feedback);
    }

    [Fact]
    public void Wrong_answer_with_no_matching_pattern_has_no_feedback()
    {
        var result = AnswerEvaluator.Evaluate(Typed(["3"], new MistakePattern(["4"], null, "f")), "99");
        Assert.False(result.IsCorrect);
        Assert.Null(result.Feedback);
    }

    [Fact]
    public void Invalid_regex_is_ignored_instead_of_crashing()
    {
        var step = Typed(["3"], new MistakePattern([], "(", "never"));
        Assert.Null(AnswerEvaluator.Evaluate(step, "x").Feedback);
    }

    [Fact]
    public void Multiple_choice_returns_the_picked_options_feedback()
    {
        Assert.Equal(new AnswerResult(true, "Right."), AnswerEvaluator.Evaluate(Choice, 0));
        Assert.Equal(new AnswerResult(false, "Off by one."), AnswerEvaluator.Evaluate(Choice, 1));
    }

    [Fact]
    public void Wrong_variant_or_out_of_range_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AnswerEvaluator.Evaluate(Choice, 2));
        Assert.Throws<InvalidOperationException>(() => AnswerEvaluator.Evaluate(Choice, "1"));
        Assert.Throws<InvalidOperationException>(() => AnswerEvaluator.Evaluate(Typed(["1"]), 0));
    }

    [Fact]
    public void Predict_steps_survive_a_pack_round_trip()
    {
        var typed = Typed(["3"], new MistakePattern(["4"], "^x$", "f"));
        var lesson = new Lesson("lesson.one", "One", "track.a", [typed, Choice with { Id = "step.c2" }]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            [new Track("track.a", "A", ["lesson.one"])], [lesson]);
        ms.Position = 0;

        var loaded = ContentPackFormat.Load(ms).Get<Lesson>("lesson.one");

        var t = Assert.IsType<PredictOutputStep>(loaded.Steps[0]);
        Assert.True(t.IsTyped);
        Assert.Equal(["3"], t.Accepted);
        Assert.Equal("f", t.Mistakes.Single().Feedback);
        Assert.Equal("^x$", t.Mistakes.Single().Regex);
        Assert.False(Assert.IsType<PredictOutputStep>(loaded.Steps[1]).IsTyped);
    }
}
