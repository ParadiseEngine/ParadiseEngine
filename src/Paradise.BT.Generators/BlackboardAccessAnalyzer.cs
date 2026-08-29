using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Paradise.BT.Generators;

/// <summary>
/// Checks that a node's DECLARED blackboard access matches what its body actually does.
///
/// <see cref="BindingGenerator"/> builds a tree's blackboard from <c>[Reads&lt;T&gt;]</c> /
/// <c>[Writes&lt;T&gt;]</c>. Those attributes have to be the contract rather than the body, because
/// a node can arrive from a referenced assembly where no body exists — attributes survive into
/// metadata and method bodies do not. The cost of that choice is that an attribute can disagree
/// with the code beside it, and the only thing catching it today is a <c>KeyNotFoundException</c>
/// on the first tick. This closes that gap without giving up the contract.
///
/// It runs in whichever assembly DECLARES the node, so every node's body is checked exactly once,
/// where it exists. A consumer in another assembly then relies on the attribute, which by then has
/// been verified.
///
/// Deliberately one-directional: it reports access the body performs and the node does not declare,
/// never a declaration the body does not use. A node may legitimately declare access it only reaches
/// down one branch, and over-declaring is refused by PBT0005/PBT0008 anyway.
///
/// It can only see calls it can resolve. A node that reaches the blackboard through a helper method
/// is not followed — see PBT0010, which refuses to guess.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlackboardAccessAnalyzer : DiagnosticAnalyzer
{
    private const string BlackboardInterface = "Paradise.BT.IBlackboard";
    private const string NodeDataInterface = "Paradise.BT.INodeData";

    internal static readonly DiagnosticDescriptor s_undeclaredAccess = new(
        id: "PBT0009",
        title: "Node uses blackboard data it does not declare",
        messageFormat: "Node '{0}' calls {1}<{2}>() but does not declare [{3}<{2}>]. The blackboard is built from the declarations, so this throws at the first tick.",
        category: "Paradise.BT.Design",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Blackboard access is declared with [Reads<T>] / [Writes<T>] so it survives into metadata for nodes consumed from other assemblies. This checks the declaration against the body.");

    internal static readonly DiagnosticDescriptor s_blackboardEscapes = new(
        id: "PBT0010",
        title: "Blackboard passed to a method whose access cannot be checked",
        messageFormat: "Node '{0}' passes its blackboard to '{1}', so what that method reads or writes cannot be attributed to this node. Access the blackboard in the node itself, or move the work into a method this node's declarations already cover.",
        category: "Paradise.BT.Design",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(s_undeclaredAccess, s_blackboardEscapes);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol target = invocation.TargetMethod;

        // The node is a struct implementing INodeData; anything else may use a blackboard freely.
        if (context.ContainingSymbol.ContainingType is not INamedTypeSymbol node
            || !Implements(node, NodeDataInterface))
        {
            return;
        }

        if (target.ContainingType?.ToDisplayString() == BlackboardInterface)
        {
            CheckDeclared(context, node, target);
            return;
        }

        CheckDoesNotEscape(context, node, invocation, target);
    }

    private static void CheckDeclared(
        OperationAnalysisContext context, INamedTypeSymbol node, IMethodSymbol target)
    {
        string attribute;
        switch (target.Name)
        {
            case "GetData":
                attribute = "Reads";
                break;
            case "SetData":
                attribute = "Writes";
                break;
            default:
                // HasData asks whether something is there and is answerable for any T.
                return;
        }

        if (target.TypeArguments.Length != 1)
        {
            return;
        }

        ITypeSymbol accessed = target.TypeArguments[0];

        // `GetData<T>()` where T is the node's own type parameter cannot be resolved to a
        // component, and guessing would produce a diagnostic nobody can act on.
        if (accessed is ITypeParameterSymbol || accessed.TypeKind == TypeKind.Error)
        {
            return;
        }

        if (Declares(node, accessed, attribute))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_undeclaredAccess,
            context.Operation.Syntax.GetLocation(),
            node.Name,
            target.Name,
            accessed.Name,
            attribute));
    }

    /// <summary>
    /// A node handing its blackboard to another method takes its access out of view. Reported
    /// rather than followed: resolving it properly means propagating an access set along the call
    /// graph, and the honest answer until then is to say the check stopped here.
    /// </summary>
    private static void CheckDoesNotEscape(
        OperationAnalysisContext context,
        INamedTypeSymbol node,
        IInvocationOperation invocation,
        IMethodSymbol target)
    {
        // The VM and the child-ticking extensions take a blackboard by design; that is the tree's
        // own plumbing, and the tree-level union covers where it leads.
        string? container = target.ContainingType?.ToDisplayString();
        if (container is "Paradise.BT.VirtualMachine" or "Paradise.BT.NodeExtensions")
        {
            return;
        }

        foreach (IArgumentOperation argument in invocation.Arguments)
        {
            if (argument.Parameter is not null
                && Implements(argument.Parameter.Type, BlackboardInterface))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    s_blackboardEscapes,
                    invocation.Syntax.GetLocation(),
                    node.Name,
                    target.Name));
                return;
            }
        }
    }

    private static bool Declares(INamedTypeSymbol node, ITypeSymbol accessed, string attribute)
    {
        foreach (AttributeData attr in node.GetAttributes())
        {
            INamedTypeSymbol? ac = attr.AttributeClass;
            if (ac is null
                || !ac.IsGenericType
                || ac.ContainingNamespace?.ToDisplayString() != "Paradise.BT"
                || ac.TypeArguments.Length != 1
                || !SymbolEqualityComparer.Default.Equals(ac.TypeArguments[0], accessed))
            {
                continue;
            }

            // A writer may also read: [Writes<T>] satisfies a GetData<T>. The reverse does not
            // hold — reading is not permission to write.
            if (ac.Name == attribute + "Attribute"
                || (attribute == "Reads" && ac.Name == "WritesAttribute"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Implements(ITypeSymbol type, string interfaceFullName)
    {
        if (type.ToDisplayString() == interfaceFullName)
        {
            return true;
        }

        if (type is ITypeParameterSymbol parameter)
        {
            foreach (ITypeSymbol constraint in parameter.ConstraintTypes)
            {
                if (Implements(constraint, interfaceFullName))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (INamedTypeSymbol i in type.AllInterfaces)
        {
            if (i.ToDisplayString() == interfaceFullName)
            {
                return true;
            }
        }

        return false;
    }
}
