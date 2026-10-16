namespace Brilliant.Code.CSharp;

/// <summary>A type's name as C# writes it: <c>int</c>, <c>int[]</c>, <c>List&lt;int&gt;</c>, <c>Dictionary&lt;string, int&gt;</c>, <c>int?</c>.</summary>
public static class TypeNames
{
    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(char)] = "char", [typeof(decimal)] = "decimal",
        [typeof(double)] = "double", [typeof(float)] = "float", [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(nint)] = "nint",
        [typeof(nuint)] = "nuint", [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(short)] = "short", [typeof(ushort)] = "ushort",
        [typeof(object)] = "object", [typeof(string)] = "string",
    };

    public static string Of(Type type)
    {
        if (Keywords.TryGetValue(type, out var keyword)) return keyword;
        if (type.IsArray) return Of(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (Nullable.GetUnderlyingType(type) is { } underlying) return Of(underlying) + "?";
        var name = type.IsNested && type.DeclaringType is { } outer && !type.IsGenericParameter ? Of(outer) + "." + Simple(type) : Simple(type);
        if (!type.IsGenericType) return name;
        var arguments = type.GetGenericArguments();
        // A nested generic repeats its outer type's arguments; show only its own.
        var own = type.IsNested && type.DeclaringType is { IsGenericType: true } owner ? arguments.Skip(owner.GetGenericArguments().Length).ToArray() : arguments;
        return own.Length == 0 ? name : name + "<" + string.Join(", ", own.Select(Of)) + ">";
    }

    private static string Simple(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }
}
