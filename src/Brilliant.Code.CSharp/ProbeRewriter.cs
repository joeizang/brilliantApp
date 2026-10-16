using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Brilliant.Code.CSharp;

/// <summary>Inserts cooperative limits into synchronous learner syntax. Generated calls always name the host runtime explicitly.</summary>
public static class ProbeRewriter
{
    /// <summary>
    /// <paramref name="trackCall"/>, when given (it needs <paramref name="captureLocals"/>), names the methods and local functions whose calls are
    /// recorded: each call is put on the call stack the snapshots carry, with its arguments, and a return is a probe of its own, with the value returned.
    /// </summary>
    public static SyntaxTree Rewrite(SyntaxTree tree, bool captureLocals = false, SemanticModel? semanticModel = null, Func<string, bool>? trackCall = null)
    {
        var root = tree.GetRoot();
        if (captureLocals && semanticModel is null)
            throw new ArgumentException("Capturing locals requires the original semantic model.", nameof(semanticModel));
        if (root.DescendantNodes().Any(n => n is DestructorDeclarationSyntax or StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax))
            throw new NotSupportedException("Finalizers and stackalloc are not supported by the bounded synchronous C# gate.");
        if (root.DescendantNodes().Any(n => n is GotoStatementSyntax))
            throw new NotSupportedException("goto is not supported by the bounded C# execution gate.");
        return tree.WithRootAndOptions(new Rewriter(captureLocals, semanticModel, captureLocals ? trackCall : null).Visit(root)!, tree.Options);
    }

    private sealed class Rewriter(bool captureLocals, SemanticModel? semanticModel, Func<string, bool>? trackCall) : CSharpSyntaxRewriter
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

        // An expression body of a tracked method or local function: its value returned as a recorded return, or a void body ended with the probe.
        private BlockSyntax TrackedArrow(SyntaxNode original, ExpressionSyntax originalExpression, ExpressionSyntax visited, TypeSyntax returnType, IMethodSymbol symbol)
        {
            var body = ArrowBody(originalExpression, visited, symbol.ReturnsVoid);
            var line = originalExpression.GetLocation().GetMappedLineSpan().StartLinePosition.Line + 1;
            if (symbol.ReturnsVoid)
                return Called(body, symbol, EndOfVoidCall(null, original, symbol, line));
            if (ProbeArguments(originalExpression) is { } arguments && body.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault() is { Expression: { } value } ret && value is not ThrowExpressionSyntax)
            {
                var call = SyntaxFactory.ParseExpression($"{Budget}.Return<{returnType.ToString().Trim()}>(__BRILLIANT_VALUE__, {arguments})");
                var placeholder = call.DescendantNodes().OfType<IdentifierNameSyntax>().First(i => i.Identifier.ValueText == "__BRILLIANT_VALUE__");
                body = body.ReplaceNode(ret, ret.WithExpression(call.ReplaceNode(placeholder, value.WithoutTrivia())));
            }
            return Called(body, symbol, null);
        }
        private static bool Void(TypeSyntax type) => type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);
        private static StatementSyntax Loop(StatementSyntax statement) => statement is BlockSyntax block
            ? block.WithStatements(block.Statements.Insert(0, Call("Tick"))) : SyntaxFactory.Block(Call("Tick"), statement);

        private static bool Capturable(ITypeSymbol type) => !type.IsRefLikeType && type.TypeKind is not (TypeKind.Pointer or TypeKind.FunctionPointer);

        // The arguments of a probe before a statement: its line, its function, and the variables in scope that are definitely assigned there.
        private string? ProbeArguments(SyntaxNode statement)
        {
            if (!captureLocals || semanticModel is null || statement is LocalFunctionStatementSyntax) return null;
            var flow = semanticModel.AnalyzeDataFlow(statement);
            var assigned = flow?.Succeeded == true ? flow.DefinitelyAssignedOnEntry : [];
            var symbols = semanticModel.LookupSymbols(statement.SpanStart)
                .Where(symbol => symbol is ILocalSymbol or IParameterSymbol)
                .Where(symbol => assigned.Contains(symbol, SymbolEqualityComparer.Default))
                .Where(symbol => symbol switch
                {
                    ILocalSymbol local => Capturable(local.Type),
                    IParameterSymbol parameter => Capturable(parameter.Type),
                    _ => false,
                }).GroupBy(symbol => symbol.Name).Select(group => group.First()).ToArray();
            return Arguments(statement.GetLocation().GetMappedLineSpan().StartLinePosition.Line + 1, FunctionName(statement), symbols);
        }

        private static string Arguments(int line, string? functionName, IEnumerable<ISymbol> symbols)
        {
            var list = symbols.ToArray();
            var names = string.Join(",", list.Select(symbol => SymbolDisplay.FormatLiteral(symbol.Name, true)));
            var values = string.Join(",", list.Select(symbol => "@" + symbol.Name));
            var function = functionName is not null ? SymbolDisplay.FormatLiteral(functionName, true) : "null";
            return $"{line}, {function}, new string[] {{{names}}}, new object?[] {{{values}}}";
        }

        private StatementSyntax? Snapshot(SyntaxNode statement) =>
            ProbeArguments(statement) is { } arguments ? SyntaxFactory.ParseStatement($"{Budget}.Snapshot({arguments});") : null;

        // ---- call tracking ----

        // The method or local function a node is directly in (not through a lambda), when its calls are tracked.
        private IMethodSymbol? TrackedFunction(SyntaxNode node)
        {
            if (trackCall is null || semanticModel is null) return null;
            foreach (var ancestor in node.Ancestors())
                switch (ancestor)
                {
                    case MethodDeclarationSyntax or LocalFunctionStatementSyntax:
                        var symbol = semanticModel.GetDeclaredSymbol(ancestor) as IMethodSymbol;
                        return symbol is not null && !symbol.ReturnsByRef && !symbol.ReturnsByRefReadonly && trackCall(symbol.Name) ? symbol : null;
                    case AnonymousFunctionExpressionSyntax or ConstructorDeclarationSyntax or DestructorDeclarationSyntax or AccessorDeclarationSyntax
                        or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax:
                        return null;
                }
            return null;
        }

        private static TypeSyntax ReturnTypeOf(SyntaxNode function) => function switch
        {
            MethodDeclarationSyntax method => method.ReturnType,
            LocalFunctionStatementSyntax local => local.ReturnType,
            _ => throw new InvalidOperationException(),
        };

        private static BlockSyntax Called(BlockSyntax body, IMethodSymbol function, string? end)
        {
            var parameters = function.Parameters.Where(p => p.RefKind != RefKind.Out && Capturable(p.Type)).ToArray();
            var names = string.Join(",", parameters.Select(p => SymbolDisplay.FormatLiteral(p.Name, true)));
            var values = string.Join(",", parameters.Select(p => "@" + p.Name));
            var inside = end is null ? body : body.WithStatements(body.Statements.Add(SyntaxFactory.ParseStatement(end)));
            return SyntaxFactory.Block(
                SyntaxFactory.ParseStatement($"{Budget}.Enter({SymbolDisplay.FormatLiteral(function.Name, true)}, new string[] {{{names}}}, new object?[] {{{values}}});"),
                SyntaxFactory.TryStatement(inside, default, SyntaxFactory.FinallyClause(SyntaxFactory.Block(Call("ExitCall")))));
        }

        // The probe that ends a void call that runs off its end: its parameters, and the locals declared at the top of its body.
        private string? EndOfVoidCall(BlockSyntax? original, SyntaxNode function, IMethodSymbol symbol, int line)
        {
            if (!symbol.ReturnsVoid || semanticModel is null) return null;
            IEnumerable<ISymbol> locals = [];
            if (original is not null)
            {
                // The body as a region says its end is unreachable inside a local function; the statements of the body say it right.
                var reaches = original.Statements.Count == 0 || semanticModel.AnalyzeControlFlow(original.Statements[0], original.Statements[^1]) is { Succeeded: true, EndPointIsReachable: true };
                if (!reaches) return null;
                var assigned = semanticModel.AnalyzeDataFlow(original) is { Succeeded: true } flow ? flow.DefinitelyAssignedOnExit : [];
                locals = original.Statements.OfType<LocalDeclarationStatementSyntax>().SelectMany(d => d.Declaration.Variables)
                    .Select(v => semanticModel.GetDeclaredSymbol(v)).OfType<ILocalSymbol>()
                    .Where(local => Capturable(local.Type) && assigned.Contains(local, SymbolEqualityComparer.Default));
            }
            var parameters = symbol.Parameters.Where(p => p.RefKind != RefKind.Out && Capturable(p.Type));
            return $"{Budget}.ReturnVoid({Arguments(line, symbol.Name, parameters.Cast<ISymbol>().Concat(locals))});";
        }

        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            var visited = (ReturnStatementSyntax)base.VisitReturnStatement(node)!;
            if (TrackedFunction(node) is not { } function || ProbeArguments(node) is not { } arguments) return visited;
            if (visited.Expression is null)
                return SyntaxFactory.Block(SyntaxFactory.ParseStatement($"{Budget}.ReturnVoid({arguments});"), visited);
            var type = ReturnTypeOf(node.Ancestors().First(a => a is MethodDeclarationSyntax or LocalFunctionStatementSyntax));
            var call = SyntaxFactory.ParseExpression($"{Budget}.Return<{type.ToString().Trim()}>(__BRILLIANT_VALUE__, {arguments})");
            var placeholder = call.DescendantNodes().OfType<IdentifierNameSyntax>().First(i => i.Identifier.ValueText == "__BRILLIANT_VALUE__");
            return visited.WithExpression(call.ReplaceNode(placeholder, visited.Expression.WithoutTrivia()));
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
            var exclude = SyntaxFactory.ParseExpression($"(object)@{name} is not global::Brilliant.Code.CSharp.BudgetExceededException");
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
            if (TrackedFunction(node.Body ?? (SyntaxNode?)node.ExpressionBody ?? node) is { } tracked && (node.Body is not null || node.ExpressionBody is not null))
            {
                var called = node.Body is not null
                    ? Called(v.Body!, tracked, EndOfVoidCall(node.Body, node, tracked, node.Body.CloseBraceToken.GetLocation().GetMappedLineSpan().StartLinePosition.Line + 1))
                    : TrackedArrow(node, node.ExpressionBody!.Expression, v.ExpressionBody!.Expression, v.ReturnType, tracked);
                return v.WithBody(called).WithExpressionBody(null).WithSemicolonToken(default);
            }
            var body = v.Body ?? (v.ExpressionBody is { } expression ? ArrowBody(node.ExpressionBody!.Expression, expression.Expression, Void(v.ReturnType)) : null);
            return body is null ? v : v.WithBody(Wrap(body)).WithExpressionBody(null).WithSemicolonToken(default);
        }
        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            var v = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
            if (TrackedFunction(node.Body ?? (SyntaxNode?)node.ExpressionBody ?? node) is { } tracked && (node.Body is not null || node.ExpressionBody is not null))
            {
                var called = node.Body is not null
                    ? Called(v.Body!, tracked, EndOfVoidCall(node.Body, node, tracked, node.Body.CloseBraceToken.GetLocation().GetMappedLineSpan().StartLinePosition.Line + 1))
                    : TrackedArrow(node, node.ExpressionBody!.Expression, v.ExpressionBody!.Expression, v.ReturnType, tracked);
                return v.WithBody(called).WithExpressionBody(null).WithSemicolonToken(default);
            }
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
