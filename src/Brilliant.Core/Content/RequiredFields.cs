using System.Collections;
using System.Reflection;

namespace Brilliant.Core.Content;

/// <summary>
/// Finds required fields that a pack leaves null or out. The content model says what is required by not marking it nullable
/// (<c>IReadOnlyList&lt;string&gt; LessonIds</c>, <c>string Id</c>); JSON can still say <c>null</c> or omit the property, and the
/// deserializer lets that through. A pack like that loads, then crashes the first screen that reads the field, so it is refused at the door.
/// </summary>
internal static class RequiredFields
{
    private const int MaxReported = 10;

    /// <summary>Paths such as <c>Tracks[0].LessonIds</c> of required values that are null in <paramref name="root"/>, at most a few.</summary>
    public static IReadOnlyList<string> Missing(object root)
    {
        var found = new List<string>();
        Walk(root, root.GetType().Name, new NullabilityInfoContext(), found);
        return found;
    }

    private static void Walk(object? value, string path, NullabilityInfoContext context, List<string> found)
    {
        if (value is null || found.Count >= MaxReported) return;
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string or decimal or DateTime or DateTimeOffset or Guid or TimeSpan) return;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0 || property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null) continue;
            var info = context.Create(property);
            var child = property.GetValue(value);
            var where = $"{path}.{property.Name}";
            if (child is null)
            {
                if (info.ReadState == NullabilityState.NotNull) found.Add(where);
                continue;
            }
            if (child is IEnumerable items and not string)
            {
                var elementsNullable = info.GenericTypeArguments.Length == 1 && info.GenericTypeArguments[0].ReadState != NullabilityState.NotNull;
                var i = 0;
                foreach (var item in items)
                {
                    if (item is null) { if (!elementsNullable) found.Add($"{where}[{i}]"); }
                    else Walk(item, $"{where}[{i}]", context, found);
                    i++;
                }
            }
            else Walk(child, where, context, found);
        }
    }
}
