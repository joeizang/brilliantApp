using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Brilliant.Runtime.CSharp;

/// <summary>A small synchronous exercise surface, not an OS sandbox. Unknown framework APIs fail closed.</summary>
internal static class NativeCodePolicy
{
    public static void Validate(SyntaxTree tree, SemanticModel model, bool allowBlockingCalls, CancellationToken cancellationToken)
    {
        foreach (var node in tree.GetRoot(cancellationToken).DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is LockStatementSyntax or AttributeSyntax ||
                node is ExpressionSyntax expression && model.GetTypeInfo(expression, cancellationToken).Type?.TypeKind == TypeKind.Dynamic)
                throw new NotSupportedException("Locks, source attributes and dynamic execution are not available in synchronous C# exercises.");
            var declared = model.GetDeclaredSymbol(node, cancellationToken);
            if (declared is INamespaceSymbol or INamedTypeSymbol &&
                (declared.Name == "Brilliant" || declared.ContainingNamespace?.ToDisplayString().StartsWith("Brilliant", StringComparison.Ordinal) == true))
                throw new NotSupportedException("The Brilliant runtime namespace is reserved and is not available for declarations.");
            var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
            if (symbol is null or INamespaceSymbol or ILocalSymbol or IParameterSymbol or ITypeParameterSymbol) continue;
            if (symbol is IAliasSymbol alias) symbol = alias.Target;
            if (SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, model.Compilation.Assembly)) continue;
            var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
            if (type is null || Allowed(type, symbol, allowBlockingCalls)) continue;
            throw new NotSupportedException($"{symbol.ToDisplayString()} is not available in synchronous C# exercises (line {node.GetLocation().GetMappedLineSpan().StartLinePosition.Line + 1}).");
        }
    }

    private static bool Allowed(INamedTypeSymbol type, ISymbol member, bool allowBlockingCalls)
    {
        var name = type.OriginalDefinition.ToDisplayString();
        // No native run may access these even if hidden behind aliases, method groups or property access.
        if (name == "System.Object" && member.Name == "GetType") return false;
        if (type.SpecialType is >= SpecialType.System_Object and <= SpecialType.System_Decimal or SpecialType.System_String) return true;
        if (name == "System.Console") return member.Name is "Write" or "WriteLine" or "Out" or "Error" or "Console";
        // A blocking call can't be unwound, so a learner's Thread.Sleep would outlast the deadline and force an app restart.
        // Only the runtime's own tests enable it, to exercise the independent watchdog.
        if (name == "System.Threading.Thread") return allowBlockingCalls && member.Name is "Sleep" or "Thread";
        if (name == "System.IO.TextWriter") return member.Name is "Write" or "WriteLine";
        var ns = type.ContainingNamespace.ToDisplayString();
        if (ns == "System.Collections.Generic")
            return type.Name is "List" or "Dictionary" or "HashSet" or "Queue" or "Stack" or "KeyValuePair"
                or "IEnumerable" or "IEnumerator" or "ICollection" or "IList" or "IDictionary" or "IReadOnlyList" or "IReadOnlyCollection" or "IReadOnlyDictionary" or "IEqualityComparer" or "IComparer"
                || type.ContainingType is { } outer && Allowed(outer, member, allowBlockingCalls);
        if (ns == "System.Collections") return type.Name is "IEnumerable" or "IEnumerator" or "IList" or "IDictionary" or "ICollection" or "DictionaryEntry";
        if (ns == "System.Linq") return type.Name == "Enumerable";
        if (ns == "System.Text") return type.Name == "StringBuilder";
        if (ns != "System") return false;
        return type.Name is "Array" or "Math" or "MathF" or "Convert" or "Enum" or "Nullable" or "ValueType" or "ValueTuple" or "Tuple"
            or "Func" or "Action" or "Predicate" or "Comparison" or "IDisposable" or "IEquatable" or "IComparable" or "IFormattable"
            or "StringComparer" or "StringComparison" or "StringSplitOptions" or "HashCode" or "Index" or "Range"
            or "DateTime" or "DateTimeOffset" or "TimeSpan" or "DayOfWeek" or "MidpointRounding"
            or "Exception" or "ArgumentException" or "ArgumentNullException" or "ArgumentOutOfRangeException" or "InvalidOperationException"
            or "NotSupportedException" or "FormatException" or "OverflowException" or "DivideByZeroException" or "IndexOutOfRangeException";
    }
}
