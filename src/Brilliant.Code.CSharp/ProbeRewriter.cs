using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Brilliant.Code.CSharp;

/// <summary>Inserts cooperative limits into synchronous learner syntax. Generated calls always name the host runtime explicitly.</summary>
public static class ProbeRewriter
{
    public static SyntaxTree Rewrite(SyntaxTree tree, bool captureLocals = false, SemanticModel? semanticModel = null)
    {
        var root = tree.GetRoot();
        if (captureLocals && semanticModel is null)
            throw new ArgumentException("Capturing locals requires the original semantic model.", nameof(semanticModel));
        if (root.DescendantNodes().Any(n => n is DestructorDeclarationSyntax or StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax))
            throw new NotSupportedException("Finalizers and stackalloc are not supported by the bounded synchronous C# gate.");
        if (root.DescendantNodes().Any(n => n is GotoStatementSyntax))
            throw new NotSupportedException("goto is not supported by the bounded C# execution gate.");
        return tree.WithRootAndOptions(new Rewriter(captureLocals, semanticModel).Visit(root)!, tree.Options);
    }

    private sealed class Rewriter(bool captureLocals, SemanticModel? semanticModel) : CSharpSyntaxRewriter
    {
        private const string Budget = "global::Brilliant.Code.CSharp.ProbeBudget.Current!";
        private static StatementSyntax Call(string method) => SyntaxFactory.ParseStatement($"{Budget}.{method}();");
        private static BlockSyntax Wrap(BlockSyntax body) => SyntaxFactory.Block(Call("Enter"),
            SyntaxFactory.TryStatement(body, default, SyntaxFactory.FinallyClause(SyntaxFactory.Block(Call("Exit")))));
        private static BlockSyntax ExpressionBody(ExpressionSyntax expression, bool returnsVoid) => SyntaxFactory.Block(
            expression is ThrowExpressionSyntax thrown ? SyntaxFactory.ThrowStatement(thrown.Expression.WithLeadingTrivia(SyntaxFactory.Space)) :
            returnsVoid ? SyntaxFactory.ExpressionStatement(expression) : SyntaxFactory.ReturnStatement(expression.WithLeadingTrivia(SyntaxFactory.Space)));
        private BlockSyntax ArrowBody(ExpressionSyntax original, ExpressionSyntax visited, bool returnsVoid)
        {
            var body = ExpressionBody(visited, returnsVoid);
            return Snapshot(original) is { } probe ? body.WithStatements(body.Statements.Insert(0, probe)) : body;
        }
        private static bool Void(TypeSyntax type) => type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);
        private static StatementSyntax Loop(StatementSyntax statement) => statement is BlockSyntax block
            ? block.WithStatements(block.Statements.Insert(0, Call("Tick"))) : SyntaxFactory.Block(Call("Tick"), statement);

        private StatementSyntax? Snapshot(SyntaxNode statement)
        {
            if (!captureLocals || semanticModel is null || statement is LocalFunctionStatementSyntax) return null;
            var flow = semanticModel.AnalyzeDataFlow(statement);
            var assigned = flow?.Succeeded == true ? flow.DefinitelyAssignedOnEntry : [];
            var symbols = semanticModel.LookupSymbols(statement.SpanStart)
                .Where(symbol => symbol is ILocalSymbol or IParameterSymbol)
                .Where(symbol => assigned.Contains(symbol, SymbolEqualityComparer.Default))
                .Where(symbol => symbol switch
                {
                    ILocalSymbol local => !local.Type.IsRefLikeType && local.Type.TypeKind is not (TypeKind.Pointer or TypeKind.FunctionPointer),
                    IParameterSymbol parameter => !parameter.Type.IsRefLikeType && parameter.Type.TypeKind is not (TypeKind.Pointer or TypeKind.FunctionPointer),
                    _ => false,
                }).GroupBy(symbol => symbol.Name).Select(group => group.First()).ToArray();
            var line = statement.GetLocation().GetMappedLineSpan().StartLinePosition.Line + 1;
            var names = string.Join(",", symbols.Select(symbol => SymbolDisplay.FormatLiteral(symbol.Name, true)));
            var values = string.Join(",", symbols.Select(symbol => "@" + symbol.Name));
            var function = FunctionName(statement) is { } name ? SymbolDisplay.FormatLiteral(name, true) : "null";
            return SyntaxFactory.ParseStatement($"{Budget}.Snapshot({line}, {function}, new string[] {{{names}}}, new object?[] {{{values}}});");
        }

        /// <summary>The method, local function, constructor or accessor the node sits in; a lambda is part of the member around it.</summary>
        private static string? FunctionName(SyntaxNode node) => node.Ancestors().Select(ancestor => ancestor switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            LocalFunctionStatementSyntax local => local.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            OperatorDeclarationSyntax @operator => "operator " + @operator.OperatorToken.ValueText,
            AccessorDeclarationSyntax { Parent.Parent: PropertyDeclarationSyntax property } accessor => property.Identifier.ValueText + "." + accessor.Keyword.ValueText,
            AccessorDeclarationSyntax { Parent.Parent: IndexerDeclarationSyntax } accessor => "this[]." + accessor.Keyword.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            _ => null,
        }).FirstOrDefault(name => name is not null);

        private IEnumerable<StatementSyntax> Statements(IEnumerable<StatementSyntax> originals)
        {
            foreach (var statement in originals)
            {
                if (Snapshot(statement) is { } probe) yield return probe;
                yield return (StatementSyntax)Visit(statement)!;
            }
        }
        private StatementSyntax Embedded(StatementSyntax original, StatementSyntax visited) =>
            original is not BlockSyntax && Snapshot(original) is { } probe ? SyntaxFactory.Block(probe, visited) : visited;

        public override SyntaxNode? VisitBlock(BlockSyntax node) => node.WithStatements(SyntaxFactory.List(Statements(node.Statements)));
        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node) =>
            node.WithStatements(SyntaxFactory.List(Statements(node.Statements)));
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        { var v = (IfStatementSyntax)base.VisitIfStatement(node)!; return v.WithStatement(Embedded(node.Statement, v.Statement)); }
        public override SyntaxNode? VisitElseClause(ElseClauseSyntax node)
        { var v = (ElseClauseSyntax)base.VisitElseClause(node)!; return v.WithStatement(Embedded(node.Statement, v.Statement)); }
        public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
        { var v = (UsingStatementSyntax)base.VisitUsingStatement(node)!; return v.WithStatement(Embedded(node.Statement, v.Statement)); }
        public override SyntaxNode? VisitLockStatement(LockStatementSyntax node)
        { var v = (LockStatementSyntax)base.VisitLockStatement(node)!; return v.WithStatement(Embedded(node.Statement, v.Statement)); }

        private bool LambdaReturnsVoid(ExpressionSyntax node) =>
            semanticModel?.GetTypeInfo(node).ConvertedType is INamedTypeSymbol { DelegateInvokeMethod: { } invoke }
                ? invoke.ReturnsVoid : throw new NotSupportedException("Expression lambdas require a semantic model for budget instrumentation.");

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        {
            var v = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;
            return v.WithBody(Wrap(v.Body is BlockSyntax block ? block : ArrowBody((ExpressionSyntax)node.Body, (ExpressionSyntax)v.Body, LambdaReturnsVoid(node))));
        }
        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        {
            var v = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;
            return v.WithBody(Wrap(v.Body is BlockSyntax block ? block : ArrowBody((ExpressionSyntax)node.Body, (ExpressionSyntax)v.Body, LambdaReturnsVoid(node))));
        }
        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
        {
            var v = (AnonymousMethodExpressionSyntax)base.VisitAnonymousMethodExpression(node)!;
            return v.WithBlock(Wrap(v.Block));
        }

        private int _catchId;
        public override SyntaxNode? VisitCatchClause(CatchClauseSyntax node)
        {
            var visited = (CatchClauseSyntax)base.VisitCatchClause(node)!;
            var name = visited.Declaration?.Identifier.ValueText;
            if (string.IsNullOrEmpty(name))
            {
                do { name = "__brilliantException" + _catchId++; }
                while (node.SyntaxTree.GetRoot().DescendantTokens().Any(t => t.ValueText == name));
            }
            var declaration = visited.Declaration ?? SyntaxFactory.CatchDeclaration(SyntaxFactory.ParseTypeName("global::System.Exception"));
            declaration = declaration.WithIdentifier(SyntaxFactory.ParseToken("@" + name).WithLeadingTrivia(SyntaxFactory.Space));
            var exclude = SyntaxFactory.ParseExpression($"@{name} is not global::Brilliant.Code.CSharp.BudgetExceededException");
            var filter = visited.Filter?.FilterExpression;
            if (filter is not null) exclude = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression,
                SyntaxFactory.ParenthesizedExpression(exclude), SyntaxFactory.ParenthesizedExpression(filter));
            return visited.WithDeclaration(declaration).WithFilter(SyntaxFactory.CatchFilterClause(exclude));
        }

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        { var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!; return visited.WithStatement(Loop(Embedded(node.Statement, visited.Statement))); }
        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        { var visited = (DoStatementSyntax)base.VisitDoStatement(node)!; return visited.WithStatement(Loop(Embedded(node.Statement, visited.Statement))); }
        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        { var visited = (ForStatementSyntax)base.VisitForStatement(node)!; return visited.WithStatement(Loop(Embedded(node.Statement, visited.Statement))); }
        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        { var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!; return visited.WithStatement(Loop(Embedded(node.Statement, visited.Statement))); }
        public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        { var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!; return visited.WithStatement(Loop(Embedded(node.Statement, visited.Statement))); }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var v = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            var body = v.Body ?? (v.ExpressionBody is { } expression ? ArrowBody(node.ExpressionBody!.Expression, expression.Expression, Void(v.ReturnType)) : null);
            return body is null ? v : v.WithBody(Wrap(body)).WithExpressionBody(null).WithSemicolonToken(default);
        }
        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            var v = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
            var body = v.Body ?? (v.ExpressionBody is { } expression ? ArrowBody(node.ExpressionBody!.Expression, expression.Expression, Void(v.ReturnType)) : null);
            return body is null ? v : v.WithBody(Wrap(body)).WithExpressionBody(null).WithSemicolonToken(default);
        }
        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            if (node.Initializer is { ArgumentList.Arguments.Count: > 0 })
                throw new NotSupportedException("Constructor initializer arguments are not supported by the bounded C# gate.");
            var v = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
            var body = v.Body ?? (v.ExpressionBody is { } expression ? ArrowBody(node.ExpressionBody!.Expression, expression.Expression, true) : null);
            return body is null ? v : v.WithBody(Wrap(body)).WithExpressionBody(null).WithSemicolonToken(default);
        }
        public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node)
        {
            var v = (DestructorDeclarationSyntax)base.VisitDestructorDeclaration(node)!;
            var body = v.Body ?? (v.ExpressionBody is { } expression ? ArrowBody(node.ExpressionBody!.Expression, expression.Expression, true) : null);
            return body is null ? v : v.WithBody(Wrap(body)).WithExpressionBody(null).WithSemicolonToken(default);
        }
        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
        {
            var v = (AccessorDeclarationSyntax)base.VisitAccessorDeclaration(node)!;
            var body = v.Body ?? (v.ExpressionBody is { } expression ? ArrowBody(node.ExpressionBody!.Expression, expression.Expression, !v.IsKind(SyntaxKind.GetAccessorDeclaration)) : null);
            return body is null ? v : v.WithBody(Wrap(body)).WithExpressionBody(null).WithSemicolonToken(default);
        }
        public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        {
            var v = (OperatorDeclarationSyntax)base.VisitOperatorDeclaration(node)!;
            if (v.Body is null && v.ExpressionBody is null) return v;
            return v.WithBody(Wrap(v.Body ?? ArrowBody(node.ExpressionBody!.Expression, v.ExpressionBody!.Expression, false))).WithExpressionBody(null).WithSemicolonToken(default);
        }
        public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
        {
            var v = (ConversionOperatorDeclarationSyntax)base.VisitConversionOperatorDeclaration(node)!;
            if (v.Body is null && v.ExpressionBody is null) return v;
            return v.WithBody(Wrap(v.Body ?? ArrowBody(node.ExpressionBody!.Expression, v.ExpressionBody!.Expression, false))).WithExpressionBody(null).WithSemicolonToken(default);
        }
        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var v = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
            return v.ExpressionBody is null ? v : v.WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(Wrap(ArrowBody(node.ExpressionBody!.Expression, v.ExpressionBody.Expression, false))))))
                .WithExpressionBody(null).WithSemicolonToken(default);
        }
        public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            var v = (IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!;
            return v.ExpressionBody is null ? v : v.WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(Wrap(ArrowBody(node.ExpressionBody!.Expression, v.ExpressionBody.Expression, false))))))
                .WithExpressionBody(null).WithSemicolonToken(default);
        }
    }
}
