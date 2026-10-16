using Brilliant.Core.Content;
using Brilliant.Lessons.UI;
using Bunit;

namespace Brilliant.Lessons.UI.Tests;

public class ExplainStepViewTests : BunitContext
{
    [Fact]
    public void Dsa_comparisons_keep_their_original_order_labels_and_languages()
    {
        var step = new ExplainStep("step.one", "One", "Body", [],
            [new("C#", "csharp", "var x = 1;"), new("Python", "python", "x = 1")]);

        var cut = Render<ExplainStepView>(p => p.Add(c => c.Step, step));

        Assert.Equal(["C#", "Python"], cut.FindAll(".comparison h3").Select(e => e.TextContent));
        Assert.Equal(["csharp", "python"], cut.FindAll(".comparison pre").Select(e => e.GetAttribute("data-language")));
        Assert.Equal(["var x = 1;", "x = 1"], cut.FindAll(".comparison code").Select(e => e.TextContent));
    }

    [Fact]
    public void Comparisons_render_any_number_of_snippets_and_escape_authored_labels_and_code()
    {
        var step = new ExplainStep("step.one", "One", "Body", [],
            [new("Before <T>", "csharp", "List<int> xs = new();"), new("After", "csharp", "List<int> xs = [];"), new("Query", "sql", "select * from xs;")]);

        var cut = Render<ExplainStepView>(p => p.Add(c => c.Step, step));

        Assert.Equal(["Before <T>", "After", "Query"], cut.FindAll(".comparison h3").Select(e => e.TextContent));
        Assert.Equal("List<int> xs = new();", cut.Find(".comparison code").TextContent);
        Assert.Equal("sql", cut.FindAll(".comparison pre")[2].GetAttribute("data-language"));
        Assert.Empty(cut.FindAll(".comparison t"));
    }

    [Fact]
    public void Explanations_without_comparisons_render_normally()
    {
        var cut = Render<ExplainStepView>(p => p.Add(c => c.Step, new ExplainStep("step.one", "One", "Body", [], null)));

        Assert.Empty(cut.FindAll(".comparison"));
        Assert.Equal("Body", cut.Find(".explain-body").TextContent.Trim());
    }
}
