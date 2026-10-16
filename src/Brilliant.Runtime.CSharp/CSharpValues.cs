using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Brilliant.Runtime.CSharp;

/// <summary>Value comparisons and diagnostics for C# exercise results without serializing away their CLR types.</summary>
public static class CSharpValues
{
    private const int MaxDepth = 64;
    private const int MaxVisits = 10_000;

    /// <summary>Compares supported result values structurally. Excessively deep or large comparisons fail closed.</summary>
    public static bool Equal(object? expected, object? actual) => new Comparison().Equal(expected, actual);

    private sealed class Comparison
    {
        private readonly HashSet<(object, object)> _path = new(new ReferencePairComparer());
        private int _remaining = MaxVisits;

        public bool Equal(object? expected, object? actual, int depth = 0)
        {
            if (--_remaining < 0) return false;
            if (ReferenceEquals(expected, actual)) return true;
            if (expected is null || actual is null || depth >= MaxDepth) return false;
            var pair = (expected, actual);
            if (!_path.Add(pair)) return true;
            try
            {
                if (expected is IDictionary leftDictionary && actual is IDictionary rightDictionary)
                {
                    if (leftDictionary.Count != rightDictionary.Count || leftDictionary.Count > MaxVisits) return false;
                    var unmatched = rightDictionary.Keys.Cast<object>().Select(key => new DictionaryEntry(key, rightDictionary[key])).ToList();
                    foreach (DictionaryEntry entry in leftDictionary)
                    {
                        var index = unmatched.FindIndex(other => Equal(entry.Key, other.Key, depth + 1) && Equal(entry.Value, other.Value, depth + 1));
                        if (index < 0) return false;
                        unmatched.RemoveAt(index);
                    }
                    return true;
                }
                if (expected is Array a && actual is Array b &&
                    (a.Rank != b.Rank || Enumerable.Range(0, a.Rank).Any(i => a.GetLength(i) != b.GetLength(i) || a.GetLowerBound(i) != b.GetLowerBound(i)))) return false;
                if (expected is IList left && actual is IList right)
                    return left.Count == right.Count && left.Count <= MaxVisits && left.Cast<object?>().Zip(right.Cast<object?>()).All(values => Equal(values.First, values.Second, depth + 1));
                if (expected is ITuple leftTuple && actual is ITuple rightTuple)
                    return leftTuple.Length == rightTuple.Length && Enumerable.Range(0, leftTuple.Length).All(i => Equal(leftTuple[i], rightTuple[i], depth + 1));
                if (IsRecord(expected.GetType()))
                    return expected.GetType() == actual.GetType() && Members(expected).Zip(Members(actual)).All(values => Equal(values.First.Value, values.Second.Value, depth + 1));
                return expected.Equals(actual);
            }
            finally { _path.Remove(pair); }
        }
    }

    /// <summary>A bounded diagnostic representation; formatting never enumerates arbitrary lazy sequences.</summary>
    public static string Format(object? value) => new Formatter().Format(value);

    private sealed class Formatter
    {
        private const int MaxCharacters = 4096;
        private readonly StringBuilder _text = new();
        private readonly HashSet<object> _path = new(ReferenceEqualityComparer.Instance);
        private int _remaining = MaxVisits;
        private bool Full => _text.Length >= MaxCharacters;

        public string Format(object? value) { Visit(value, 0); return _text.ToString(); }

        private void Append(string value)
        {
            var available = MaxCharacters - _text.Length;
            if (available <= 0) return;
            if (value.Length <= available) _text.Append(value);
            else { _text.Append(value.AsSpan(0, available - 1)); _text.Append('…'); }
        }

        private void Visit(object? item, int depth)
        {
            if (Full) return;
            if (--_remaining < 0 || depth >= MaxDepth) { Append("<limit>"); return; }
            if (item is null) { Append("null"); return; }
            if (item is string text)
            {
                Append(System.Text.Json.JsonSerializer.Serialize(text.Length <= MaxCharacters ? text : text[..MaxCharacters]));
                return;
            }
            if (item is char character)
            {
                Append("'" + System.Text.Json.JsonSerializer.Serialize(character.ToString())[1..^1] + "'");
                return;
            }
            if (item is bool boolean) { Append(boolean ? "true" : "false"); return; }
            if (!item.GetType().IsValueType && !_path.Add(item)) { Append("<cycle>"); return; }
            try
            {
                if (item is IDictionary dictionary)
                {
                    Append("{ ");
                    Join(dictionary.Keys.Cast<object>(), key => { Visit(key, depth + 1); Append(": "); Visit(dictionary[key], depth + 1); });
                    Append(" }");
                }
                else if (item is IList list)
                {
                    Append("["); Join(list.Cast<object?>(), value => Visit(value, depth + 1)); Append("]");
                }
                else if (item is ITuple tuple)
                {
                    Append("("); Join(Enumerable.Range(0, tuple.Length), index => Visit(tuple[index], depth + 1)); Append(")");
                }
                else if (IsRecord(item.GetType()))
                {
                    Append(item.GetType().Name + " { ");
                    Join(Members(item), member => { Append(member.Name + " = "); Visit(member.Value, depth + 1); });
                    Append(" }");
                }
                else Append(Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture) ?? "null");
            }
            finally { if (!item.GetType().IsValueType) _path.Remove(item); }
        }

        private void Join<T>(IEnumerable<T> items, Action<T> write)
        {
            var first = true;
            foreach (var item in items)
            {
                if (Full) break;
                if (!first) Append(", ");
                if (_remaining <= 0) { Append("<limit>"); break; }
                write(item);
                first = false;
            }
        }
    }

    private sealed class ReferencePairComparer : IEqualityComparer<(object Left, object Right)>
    {
        public bool Equals((object Left, object Right) x, (object Left, object Right) y) => ReferenceEquals(x.Left, y.Left) && ReferenceEquals(x.Right, y.Right);
        public int GetHashCode((object Left, object Right) pair) => HashCode.Combine(RuntimeHelpers.GetHashCode(pair.Left), RuntimeHelpers.GetHashCode(pair.Right));
    }

    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$") is not null ||
        type.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(StringBuilder)]) is { } print &&
        print.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    private static IEnumerable<(string Name, object? Value)> Members(object value) =>
        value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0)
            .Select(property => (property.Name, property.GetValue(value)))
            .Concat(value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(field => (field.Name, field.GetValue(value))))
            .OrderBy(member => member.Name, StringComparer.Ordinal);
}
