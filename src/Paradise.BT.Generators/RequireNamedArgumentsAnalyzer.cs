using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Paradise.BT.Generators;

/// <summary>
/// Enforces <c>[RequireNamedArguments]</c>: a call passing two or more VALUE arguments must name
/// them all. Generated builder parameters mirror a node's surface, so two positional floats
/// transpose silently when that surface changes; one value argument cannot, and child arguments
/// are type-distinct, so both stay positional.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RequireNamedArgumentsAnalyzer : DiagnosticAnalyzer
{
    private const string AttributeFullName = "Paradise.BT.RequireNamedArgumentsAttribute";
    private const string TreeNodeFullName = "Paradise.BT.Builder.BTreeNode";

    private static readonly DiagnosticDescriptor s_rule = new(
        id: "PBT0013",
        title: "Builder arguments must be named",
        messageFormat: "Name the '{0}' argument — '{1}' takes several values, and positional "
            + "arguments transpose silently when the node's surface changes",
        category: "Paradise.BT.Design",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [s_rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(
            static ctx => Check(ctx, ((IInvocationOperation)ctx.Operation).TargetMethod,
                ((IInvocationOperation)ctx.Operation).Arguments),
            OperationKind.Invocation);
        context.RegisterOperationAction(
            static ctx => Check(ctx, ((IObjectCreationOperation)ctx.Operation).Constructor,
                ((IObjectCreationOperation)ctx.Operation).Arguments),
            OperationKind.ObjectCreation);
    }

    private static void Check(
        OperationAnalysisContext ctx, IMethodSymbol? method, ImmutableArray<IArgumentOperation> arguments)
    {
        if (method is null || !HasAttribute(method))
        {
            return;
        }

        // Only arguments the caller actually wrote, and only value ones — a child (anything
        // deriving from BTreeNode, or the params children span) cannot transpose with a value.
        var valueArguments = arguments
            .Where(a => !a.IsImplicit && a.Parameter is { } p && !p.IsParams && !IsTreeNode(p.Type))
            .ToImmutableArray();
        if (valueArguments.Length < 2)
        {
            return;
        }

        string builderName = method.MethodKind == MethodKind.Constructor
            ? method.ContainingType.Name
            : method.Name;
        foreach (IArgumentOperation argument in valueArguments)
        {
            if (argument.Syntax is ArgumentSyntax { NameColon: null } syntax)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    s_rule, syntax.GetLocation(), argument.Parameter!.Name, builderName));
            }
        }
    }

    private static bool HasAttribute(IMethodSymbol method) =>
        method.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == AttributeFullName);

    private static bool IsTreeNode(ITypeSymbol type)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == TreeNodeFullName)
            {
                return true;
            }
        }

        return false;
    }
}
