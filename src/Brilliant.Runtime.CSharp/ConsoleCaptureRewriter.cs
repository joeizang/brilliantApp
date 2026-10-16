using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Brilliant.Runtime.CSharp;

internal sealed class ConsoleCaptureRewriter(SemanticModel model) : CSharpSyntaxRewriter
{
    private static bool IsConsole(ISymbol? symbol) => symbol?.ContainingType?.ToDisplayString() == "System.Console";
    private static SyntaxNode Replace(SimpleNameSyntax name, SyntaxNode original)
    {
        if (name.Identifier.ValueText is not ("Write" or "WriteLine" or "Out" or "Error"))
            throw new NotSupportedException("C# exercises support Console.Write/WriteLine and output writers; interactive input and process console changes are unavailable.");
        return SyntaxFactory.ParseExpression("global::Brilliant.Code.CSharp.ProbeConsole." + name).WithTriviaFrom(original);
    }
    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node) =>
        IsConsole(model.GetSymbolInfo(node).Symbol) ? Replace(node.Name, node) : base.VisitMemberAccessExpression(node);
    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) =>
        IsConsole(model.GetSymbolInfo(node).Symbol) ? Replace(node, node) : base.VisitIdentifierName(node);
}
