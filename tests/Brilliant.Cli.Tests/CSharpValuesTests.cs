using Brilliant.Runtime.CSharp;
using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Brilliant.Cli.Tests;

public sealed class CSharpValuesTests
{
    [Fact]
    public void Arrays_and_nested_lists_compare_by_their_values()
    {
        Assert.True(CSharpValues.Equal(new object[] { 1, new List<int> { 2, 3 } }, new object[] { 1, new List<int> { 2, 3 } }));
        Assert.False(CSharpValues.Equal(new[] { 1, 2 }, new[] { 2, 1 }));
        Assert.False(CSharpValues.Equal(new[] { 1 }, new[] { 1, 2 }));
    }
    [Fact]
    public void Dictionaries_ignore_entry_order_but_compare_keys_and_nested_values()
    {
        var first = new Dictionary<string, object> { ["a"] = new[] { 1, 2 }, ["b"] = new List<int> { 3 } };
        var second = new Dictionary<string, object> { ["b"] = new List<int> { 3 }, ["a"] = new[] { 1, 2 } };
        Assert.True(CSharpValues.Equal(first, second));
        second["a"] = new[] { 9 };
        Assert.False(CSharpValues.Equal(first, second));
        Assert.False(CSharpValues.Equal(first, new Dictionary<string, object> { ["A"] = first["a"], ["b"] = first["b"] }));
    }
    private sealed record Result(string Name, int[] Values, List<(string, int[])> Rows);
    private readonly record struct SmallResult(int[] Values);
    private sealed class PlainResult(int[] values) { public int[] Values { get; } = values; }

    [Fact]
    public void Records_and_tuples_compare_nested_values_without_flattening_arbitrary_objects()
    {
        Assert.True(CSharpValues.Equal(new Result("a", [1], [("row", new[] { 2 })]), new Result("a", [1], [("row", new[] { 2 })])));
        Assert.False(CSharpValues.Equal(new Result("a", [1], []), new Result("a", [2], [])));
        Assert.True(CSharpValues.Equal(new SmallResult([1]), new SmallResult([1])));
        Assert.True(CSharpValues.Equal((1, new[] { 2 }), (1, new[] { 2 })));
        Assert.True(CSharpValues.Equal(Tuple.Create(1, new[] { 2 }), Tuple.Create(1, new[] { 2 })));
        Assert.False(CSharpValues.Equal(new PlainResult([1]), new PlainResult([1])));
        Assert.False(CSharpValues.Equal(new SmallResult([1]), new Result("a", [1], [])));
    }
    [Fact]
    public void Deeply_nested_results_stop_at_a_bounded_depth()
    {
        object left = 1, right = 1;
        for (var i = 0; i < 200; i++) { left = new object[] { left }; right = new object[] { right }; }
        Assert.False(CSharpValues.Equal(left, right));
    }
    [Fact]
    public void Diagnostics_show_dictionary_values_and_record_members_structurally()
    {
        Assert.Equal("{ \"a\": [1, 2] }", CSharpValues.Format(new Dictionary<string, object> { ["a"] = new[] { 1, 2 } }));
        Assert.Equal("SmallResult { Values = [1, 2] }", CSharpValues.Format(new SmallResult([1, 2])));
        Assert.Equal("(1, [2, 3])", CSharpValues.Format((1, new[] { 2, 3 })));
    }
    [Fact]
    public void Cycles_compare_and_format_without_recursing_forever()
    {
        var left = new List<object>(); left.Add(left); left.Add(1);
        var right = new List<object>(); right.Add(right); right.Add(1);
        Assert.True(CSharpValues.Equal(left, right));
        Assert.Equal("[<cycle>, 1]", CSharpValues.Format(left));
        right[1] = 2;
        Assert.False(CSharpValues.Equal(left, right));
        var a = new Dictionary<string, object>(); a["self"] = a;
        var b = new Dictionary<string, object>(); b["self"] = b;
        Assert.True(CSharpValues.Equal(a, b));
        Assert.Equal("{ \"self\": <cycle> }", CSharpValues.Format(a));
    }

    [Fact]
    public void Diagnostics_are_bounded_for_wide_and_long_results()
    {
        Assert.True(CSharpValues.Format(new string('a', 100_000)).Length <= 4096);
        Assert.True(CSharpValues.Format(Enumerable.Repeat(new[] { 1, 2 }, 20_000).ToArray()).Length <= 4096);
    }

    [Fact]
    public void Scalars_retain_their_clr_equality_and_diagnostics_are_culture_independent()
    {
        Assert.True(CSharpValues.Equal(null, null));
        Assert.False(CSharpValues.Equal(null, "null"));
        Assert.False(CSharpValues.Equal("1", 1));
        Assert.True(CSharpValues.Equal(double.NaN, double.NaN));
        Assert.False(CSharpValues.Equal(1, 1L));
        Assert.False(CSharpValues.Equal(1, 1.0));
        Assert.False(CSharpValues.Equal(new int[1, 2], new int[2, 1]));
        Assert.True(CSharpValues.Equal(new int[,] { { 1, 2 } }, new int[,] { { 1, 2 } }));
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("1.5", CSharpValues.Format(1.5m));
            Assert.Equal("null", CSharpValues.Format(null));
            Assert.Equal("\"line\\nnext\"", CSharpValues.Format("line\nnext"));
            Assert.Equal("true", CSharpValues.Format(true));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
    [Fact]
    public void Readonly_collection_implementations_keep_structural_values()
    {
        var first = new Dictionary<string, int[]> { ["a"] = [1, 2] };
        var second = new Dictionary<string, int[]> { ["a"] = [1, 2] };
        Assert.True(CSharpValues.Equal(first.ToFrozenDictionary(), second.ToFrozenDictionary()));
        Assert.True(CSharpValues.Equal(first.ToImmutableDictionary(), second.ToImmutableDictionary()));
        Assert.True(CSharpValues.Equal(ImmutableList.Create(1, 2), ImmutableList.Create(1, 2)));
        Assert.Equal("{ \"a\": [1, 2] }", CSharpValues.Format(first.ToFrozenDictionary()));
    }

    private sealed record MutableRecord { public MutableRecord? Next { get; set; } public int[] Values = [1]; }

    [Fact]
    public void Record_cycles_and_public_fields_are_structural_and_shared_children_are_not_cycles()
    {
        var first = new MutableRecord(); first.Next = first;
        var second = new MutableRecord(); second.Next = second;
        Assert.True(CSharpValues.Equal(first, second));
        Assert.Equal("MutableRecord { Next = <cycle>, Values = [1] }", CSharpValues.Format(first));
        second.Values = [2];
        Assert.False(CSharpValues.Equal(first, second));
        var shared = new[] { 1 };
        Assert.Equal("[[1], [1]]", CSharpValues.Format(new[] { shared, shared }));
    }
}
