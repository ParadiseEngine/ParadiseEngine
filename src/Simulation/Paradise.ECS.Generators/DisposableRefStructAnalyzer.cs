using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Paradise.ECS.Generators;

/// <summary>
/// Reports disposable ref-struct values without a recognized using declaration or later Dispose call.
/// </summary>
/// <remarks>This is a syntactic check, not a proof of disposal on every control-flow path.</remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DisposableRefStructAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.DisposableRefStructNotDisposed);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeLocalDeclaration, SyntaxKind.LocalDeclarationStatement);

        // Analyze expression statements (discarded return values)
        context.RegisterSyntaxNodeAction(AnalyzeExpressionStatement, SyntaxKind.ExpressionStatement);

        context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);

        // Analyze member access on invocations (e.g., GetComponent<T>().Value = x)
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context)
    {
        var localDeclaration = (LocalDeclarationStatementSyntax)context.Node;

        if (localDeclaration.UsingKeyword != default)
            return;

        if (localDeclaration.Parent is BlockSyntax { Parent: UsingStatementSyntax })
            return;

        foreach (var variable in localDeclaration.Declaration.Variables)
        {
            if (variable.Initializer?.Value == null)
                continue;

            var typeInfo = context.SemanticModel.GetTypeInfo(variable.Initializer.Value, context.CancellationToken);
            if (typeInfo.Type is not INamedTypeSymbol typeSymbol)
                continue;

            if (!IsDisposableRefStruct(typeSymbol))
                continue;

            var symbol = context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken);
            if (symbol != null && IsDisposedInScope(localDeclaration, symbol, context.SemanticModel, context.CancellationToken))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.DisposableRefStructNotDisposed,
                variable.GetLocation(),
                typeSymbol.Name));
        }
    }

    private static void AnalyzeExpressionStatement(SyntaxNodeAnalysisContext context)
    {
        var expressionStatement = (ExpressionStatementSyntax)context.Node;

        if (expressionStatement.Expression is not InvocationExpressionSyntax invocation)
            return;

        var typeInfo = context.SemanticModel.GetTypeInfo(invocation, context.CancellationToken);
        if (typeInfo.Type is not INamedTypeSymbol typeSymbol)
            return;

        if (!IsDisposableRefStruct(typeSymbol))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.DisposableRefStructNotDisposed,
            invocation.GetLocation(),
            typeSymbol.Name));
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;

        // Skip if this is part of a declaration (handled by AnalyzeLocalDeclaration)
        if (assignment.Parent is EqualsValueClauseSyntax)
            return;

        if (assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "_" })
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(assignment.Right, context.CancellationToken);
            if (typeInfo.Type is INamedTypeSymbol typeSymbol && IsDisposableRefStruct(typeSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.DisposableRefStructNotDisposed,
                    assignment.GetLocation(),
                    typeSymbol.Name));
            }
            return;
        }

        var rhsTypeInfo = context.SemanticModel.GetTypeInfo(assignment.Right, context.CancellationToken);
        if (rhsTypeInfo.Type is not INamedTypeSymbol rhsTypeSymbol)
            return;

        if (!IsDisposableRefStruct(rhsTypeSymbol))
            return;

        var lhsSymbol = context.SemanticModel.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol;
        if (lhsSymbol == null)
            return;

        var containingBlock = assignment.FirstAncestorOrSelf<BlockSyntax>();
        if (containingBlock == null)
            return;

        if (IsDisposedAfterStatement(assignment, lhsSymbol, containingBlock, context.SemanticModel, context.CancellationToken))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.DisposableRefStructNotDisposed,
            assignment.GetLocation(),
            rhsTypeSymbol.Name));
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;

        // We're looking for patterns like: GetComponent<T>().Value
        // where the expression is an invocation returning a disposable ref struct
        if (memberAccess.Expression is not InvocationExpressionSyntax invocation)
            return;

        // Skip if this is a Dispose() call - that's the correct usage
        if (memberAccess.Name.Identifier.ValueText == "Dispose")
            return;

        var typeInfo = context.SemanticModel.GetTypeInfo(invocation, context.CancellationToken);
        if (typeInfo.Type is not INamedTypeSymbol typeSymbol)
            return;

        if (!IsDisposableRefStruct(typeSymbol))
            return;

        // The return value of a method that returns a disposable ref struct is being used
        // for member access without being stored in a variable (so it can't be disposed)
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.DisposableRefStructNotDisposed,
            invocation.GetLocation(),
            typeSymbol.Name));
    }

    private static bool IsDisposableRefStruct(INamedTypeSymbol typeSymbol)
    {
        if (!typeSymbol.IsRefLikeType)
            return false;

        foreach (var member in typeSymbol.GetMembers("Dispose"))
        {
            if (member is IMethodSymbol { Parameters.Length: 0, ReturnsVoid: true })
                return true;
        }

        return false;
    }

    private static bool IsDisposedInScope(
        LocalDeclarationStatementSyntax declaration,
        ISymbol variableSymbol,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        var containingBlock = declaration.Parent as BlockSyntax;
        if (containingBlock == null)
            return false;

        return IsDisposedAfterStatement(declaration, variableSymbol, containingBlock, semanticModel, cancellationToken);
    }

    private static bool IsDisposedAfterStatement(
        SyntaxNode statement,
        ISymbol variableSymbol,
        BlockSyntax containingBlock,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        bool foundStatement = false;

        foreach (var sibling in containingBlock.Statements)
        {
            if (!foundStatement)
            {
                if (sibling == statement || sibling.Contains(statement))
                {
                    foundStatement = true;
                }
                continue;
            }

            if (HasDisposeCall(sibling, variableSymbol, semanticModel, cancellationToken))
                return true;
        }

        return false;
    }

    private static bool HasDisposeCall(
        SyntaxNode node,
        ISymbol variableSymbol,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        foreach (var invocation in node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.ValueText == "Dispose")
            {
                var targetSymbol = semanticModel.GetSymbolInfo(memberAccess.Expression, cancellationToken).Symbol;
                if (SymbolEqualityComparer.Default.Equals(targetSymbol, variableSymbol))
                    return true;
            }
        }

        return false;
    }
}
