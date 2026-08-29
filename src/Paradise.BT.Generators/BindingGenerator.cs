using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Paradise.BT.Generators;

/// <summary>
/// Emits each tree's blackboard: collects the node types an <c>IBehaviorTreeBuilder</c>
/// implementation names, unions their access, and emits a <c>{Type}Blackboard</c> plus its
/// <c>Bind</c>. The interface rather than an attribute, so the shape is compile-checked — a tree
/// type must have a <c>Build</c> — and a tree can be a type parameter.
///
/// The union of the nodes' access IS the tree's contract — there is no hand-maintained row to
/// check against, so nothing can drift stale when a node is added or removed. The one rule left
/// is that a component may not be written (PBT0008): components bind read-only by value.
///
/// Takes no reference on Paradise.ECS and could not: a type is a component when it carries
/// [Component] or implements an interface NAMED Paradise.ECS.IComponent. Everything else is a
/// caller-supplied extra.
/// </summary>
[Generator]
public sealed class BindingGenerator : IIncrementalGenerator
{
    private const string TreeBuilderInterfaceName = "IBehaviorTreeBuilder";
    private const string TreeBuilderInterfaceNamespace = "Paradise.BT.Builder";
    private const string BindingAttributeFullName = "Paradise.BT.BehaviorTreeBindingAttribute";
    private const string NodeDataInterface = "Paradise.BT.INodeData";
    private const string ComponentInterface = "Paradise.ECS.IComponent";
    private const string BlackboardInterface = "Paradise.BT.IBlackboard";

    private static readonly DiagnosticDescriptor s_optionalUnsupported = new(
        id: "PBT0006",
        title: "Optional component access is not supported",
        messageFormat: "Node '{0}' declares [OptionalReads<{1}>], which is not supported yet: the ECS emits optional accessors on a queryable's per-entity view only, never on the Segments view a world system iterates. Use [Reads<{1}>] and a required claim.",
        category: "Paradise.BT.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_componentWriteUnsupported = new(
        id: "PBT0008",
        title: "Node writes a component",
        messageFormat: "Node '{0}' writes component '{1}'. Components bind read-only by value, so "
            + "the write could not reach the chunk — write a conclusion the caller applies instead.",
        category: "Paradise.BT.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var bindings = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) =>
                    node is TypeDeclarationSyntax { BaseList.Types.Count: > 0 },
                transform: static (ctx, ct) => GetBinding(ctx, ct))
            .Where(static b => b.HasValue)
            .Select(static (b, _) => b!.Value);

        // The builders BTreeNodeGenerator will emit for nodes declared here. Derived from the same
        // [Builder] declarations it reads, because its output is not visible to this generator —
        // so a tree saying `new ThreatNear(0.1f)` is recovered by name against this table.
        var builders = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) =>
                    BTreeNodeGenerator.IsStructDeclaration(node)
                    && ((TypeDeclarationSyntax)node) is { AttributeLists.Count: > 0, BaseList.Types.Count: > 0 },
                transform: static (ctx, ct) => GetBuilder(ctx, ct))
            .Where(static b => b.HasValue)
            .Select(static (b, _) => b!.Value);

        context.RegisterSourceOutput(
            bindings.Collect().Combine(builders.Collect()),
            static (spc, data) => Emit(spc, data.Left, data.Right));
    }

    // ===================== collection =====================

    /// <summary>
    /// What one node type declares it touches, read straight off the SYMBOL.
    ///
    /// Off the symbol, not off a syntax scan of the compilation, and that distinction is the whole
    /// point: a node can live in a referenced assembly. <c>DelayTimerNode</c> ships in
    /// Paradise.BT.Nodes and has no declaration syntax here at all, so a source-only pass would
    /// silently give it no access and leave its delta time out of the blackboard.
    /// </summary>
    private static ImmutableArray<Access> CollectAccess(
        INamedTypeSymbol symbol, Compilation compilation, System.Threading.CancellationToken ct)
    {
        var access = ImmutableArray.CreateBuilder<Access>();
        ScanBody(symbol, compilation, access, ct);
        foreach (var attr in symbol.GetAttributes())
        {
            INamedTypeSymbol? ac = attr.AttributeClass;
            if (ac is null
                || !ac.IsGenericType
                || ac.ContainingNamespace?.ToDisplayString() != "Paradise.BT")
            {
                continue;
            }

            AccessKind kind;
            switch (ac.Name)
            {
                case "ReadsAttribute":
                    kind = AccessKind.Read;
                    break;
                case "WritesAttribute":
                    kind = AccessKind.Write;
                    break;
                case "OptionalReadsAttribute":
                    kind = AccessKind.OptionalRead;
                    break;
                default:
                    continue;
            }

            ITypeSymbol t = ac.TypeArguments[0];
            access.Add(new Access(
                symbol.Name, t.ToDisplayString(), t.Name, kind, IsComponent(t)));
        }

        // The generated counterpart of those attributes: the node's DECLARING assembly publishes
        // its body-scanned access as [assembly: NodeAccess(...)], so a cross-assembly node needs
        // no hand-written declarations at all. Duplicates with the sources above are harmless —
        // the blackboard merges per type.
        foreach (AttributeData attr in symbol.ContainingAssembly.GetAttributes())
        {
            ct.ThrowIfCancellationRequested();
            if (attr.AttributeClass?.ToDisplayString() != "Paradise.BT.NodeAccessAttribute"
                || attr.ConstructorArguments.Length != 1
                || attr.ConstructorArguments[0].Value is not INamedTypeSymbol node
                || !SymbolEqualityComparer.Default.Equals(node, symbol))
            {
                continue;
            }

            foreach (System.Collections.Generic.KeyValuePair<string, TypedConstant> named
                in attr.NamedArguments)
            {
                AccessKind kind;
                switch (named.Key)
                {
                    case "Reads":
                        kind = AccessKind.Read;
                        break;
                    case "Writes":
                        kind = AccessKind.Write;
                        break;
                    default:
                        continue;
                }

                if (named.Value.Values.IsDefault)
                {
                    continue;
                }

                foreach (TypedConstant value in named.Value.Values)
                {
                    if (value.Value is ITypeSymbol t)
                    {
                        access.Add(new Access(
                            symbol.Name, t.ToDisplayString(), t.Name, kind, IsComponent(t)));
                    }
                }
            }
        }

        return access.ToImmutable();
    }

    /// <summary>
    /// The builder BTreeNodeGenerator will emit for a <c>[Builder]</c> node, as (name, access).
    ///
    /// The naming rule is duplicated from that generator — an optional name argument, else the
    /// type name with a trailing "Node" removed — because the two cannot share a computed value
    /// across the generator boundary. If that rule changes there, it changes here.
    /// </summary>
    private static BuilderModel? GetBuilder(
        GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ctx.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)ctx.Node, ct)
                is not INamedTypeSymbol symbol
            || !Implements(symbol, NodeDataInterface))
        {
            return null;
        }

        string? name = null;
        bool isBuilder = false;
        foreach (AttributeData attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() != "Paradise.BT.BuilderAttribute")
            {
                continue;
            }

            isBuilder = true;
            foreach (TypedConstant argument in attr.ConstructorArguments)
            {
                if (argument.Type?.SpecialType == SpecialType.System_String
                    && argument.Value is string given)
                {
                    name = given;
                }
            }
        }

        if (!isBuilder)
        {
            return null;
        }

        name ??= symbol.Name.EndsWith("Node", System.StringComparison.Ordinal)
            ? symbol.Name.Substring(0, symbol.Name.Length - 4)
            : symbol.Name;

        return new BuilderModel(
            name, CollectAccess(symbol, ctx.SemanticModel.Compilation, ct));
    }

    /// <summary>
    /// Read a node's access out of its BODY: <c>GetData</c> is a read, <c>SetData</c> a write.
    ///
    /// Only where the body EXISTS, which is the rule: a node declared here is scanned and needs no
    /// attributes; one from a referenced assembly has no syntax and is read from its attributes.
    /// <c>DeclaringSyntaxReferences</c> is that test. The two are unioned, so a node reaching the
    /// blackboard somewhere this cannot follow (PBT0010) can still say so by hand.
    /// </summary>
    private static void ScanBody(
        INamedTypeSymbol symbol,
        Compilation compilation,
        ImmutableArray<Access>.Builder access,
        System.Threading.CancellationToken ct)
    {
        foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            SyntaxNode declaration = reference.GetSyntax(ct);
            SemanticModel model = compilation.GetSemanticModel(declaration.SyntaxTree);

            foreach (InvocationExpressionSyntax invocation in
                declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                ct.ThrowIfCancellationRequested();
                if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol target
                    || target.ContainingType?.ToDisplayString() != BlackboardInterface
                    || target.TypeArguments.Length != 1)
                {
                    continue;
                }

                AccessKind kind;
                switch (target.Name)
                {
                    case "GetData":
                        kind = AccessKind.Read;
                        break;
                    case "SetData":
                        kind = AccessKind.Write;
                        break;
                    default:
                        // HasData asks whether something is there and is answerable for any T,
                        // so it is not a claim on anything.
                        continue;
                }

                ITypeSymbol t = target.TypeArguments[0];

                // `GetData<T>()` where T is the node's own type parameter names no component and
                // cannot be resolved to a field.
                if (t is ITypeParameterSymbol || t.TypeKind == TypeKind.Error)
                {
                    continue;
                }

                access.Add(new Access(
                    symbol.Name, t.ToDisplayString(), t.Name, kind, IsComponent(t)));
            }
        }
    }

    private static BindingModel? GetBinding(
        GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var decl = (TypeDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(decl, ct) is not INamedTypeSymbol symbol
            || !BuildsATree(symbol))
        {
            return null;
        }

        // Every node type the type MENTIONS, in any spelling: `new StrikeNode()`, `Node<T>(...)`,
        // a `typeof`. All of them surface as a TypeSyntax, so one sweep catches each. Deliberately
        // an over-approximation: a stray mention widens the blackboard, never silently misses.
        var access = ImmutableArray.CreateBuilder<Access>();
        var unresolved = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (TypeSyntax ts in decl.DescendantNodes().OfType<TypeSyntax>())
        {
            ct.ThrowIfCancellationRequested();

            // GetSymbolInfo, not GetTypeInfo: an identifier standing in TYPE position is not an
            // expression, so GetTypeInfo returns null for the `SeekNode` in `new SeekNode()` — the
            // single most common way a tree names a node. GetTypeInfo is still worth asking as a
            // fallback, for positions where the type is inferred rather than named.
            ITypeSymbol? resolved = ctx.SemanticModel.GetSymbolInfo(ts, ct).Symbol as ITypeSymbol
                ?? ctx.SemanticModel.GetTypeInfo(ts, ct).Type;

            if (resolved is not INamedTypeSymbol t || t.TypeKind == TypeKind.Error)
            {
                // An unresolvable name in a tree is very often a builder GENERATED for a node in
                // this same compilation — `new ThreatNear(0.1f)`. BTreeNodeGenerator emits it,
                // this generator cannot see it, and the reference is an error type here even
                // though the finished compilation is fine.
                //
                // Recovered by NAME against the builder table below, which is derived from the
                // same [Builder] declarations BTreeNodeGenerator reads. Paradise.ECS does exactly
                // this where SystemGenerator meets QueryableGenerator's output.
                if (ts is IdentifierNameSyntax or GenericNameSyntax)
                {
                    unresolved.Add(((SimpleNameSyntax)ts).Identifier.ValueText);
                }

                continue;
            }

            // A builder wraps its node as a generic argument — `Sequence : CompositeNode<SequenceNode>`
            // — so the DSL names the node type after all, in metadata rather than in the tree's
            // source. Following it is what lets a tree written with the builder DSL be bound.
            INamedTypeSymbol? node = Implements(t, NodeDataInterface) ? t : BuiltNodeOf(t);

            if (node is not null && seen.Add(node.ToDisplayString()))
            {
                access.AddRange(CollectAccess(node, ctx.SemanticModel.Compilation, ct));
            }
        }

        // Nodes a FACTORY builds, which the sweep above cannot see: a method returning a definition
        // discards what it built, so the tree calling it names no node.
        // The factory knows, and says so with [Builds<T>] — read here off the resolved method, so
        // it works for a factory in a referenced assembly exactly as for one in source.
        foreach (InvocationExpressionSyntax invocation in
            decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol factory)
            {
                continue;
            }

            // A factory RETURNING a builder keeps the node type: `Seq(…)` is typed Sequence, which
            // is CompositeNode<SequenceNode>. That is the difference between this and the factories
            // that were deleted, which returned a bare definition and told you nothing.
            if (factory.ReturnType is INamedTypeSymbol returned
                && BuiltNodeOf(returned) is INamedTypeSymbol returnedNode
                && seen.Add(returnedNode.ToDisplayString()))
            {
                access.AddRange(CollectAccess(returnedNode, ctx.SemanticModel.Compilation, ct));
            }

            foreach (AttributeData built in factory.GetAttributes())
            {
                INamedTypeSymbol? ac = built.AttributeClass;
                if (ac is null
                    || !ac.IsGenericType
                    || ac.Name != "BuildsAttribute"
                    || ac.ContainingNamespace?.ToDisplayString() != "Paradise.BT"
                    || ac.TypeArguments[0] is not INamedTypeSymbol builds
                    || !Implements(builds, NodeDataInterface)
                    || !seen.Add(builds.ToDisplayString()))
                {
                    continue;
                }

                access.AddRange(CollectAccess(builds, ctx.SemanticModel.Compilation, ct));
            }
        }

        // The escape hatch: [BehaviorTreeBinding(Also = ...)] names nodes the tree composes only
        // through a factory nobody annotated — the one form the sweep cannot see.
        foreach (AttributeData bindingAttr in symbol.GetAttributes())
        {
            if (bindingAttr.AttributeClass?.ToDisplayString() != BindingAttributeFullName)
            {
                continue;
            }

            foreach (var named in bindingAttr.NamedArguments)
            {
                if (named.Key != "Also" || named.Value.Kind != TypedConstantKind.Array)
                {
                    continue;
                }

                foreach (TypedConstant entry in named.Value.Values)
                {
                    if (entry.Value is INamedTypeSymbol also
                        && Implements(also, NodeDataInterface)
                        && seen.Add(also.ToDisplayString()))
                    {
                        access.AddRange(CollectAccess(also, ctx.SemanticModel.Compilation, ct));
                    }
                }
            }
        }

        string ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();

        return new BindingModel(
            ns,
            symbol.Name,
            access.ToImmutable(),
            unresolved.ToImmutable(),
            decl.Identifier.GetLocation());
    }

    /// <summary>A tree type is one implementing <c>IBehaviorTreeBuilder</c> (either arity), by
    /// name — the interface lives in Paradise.BT.Builder, which this generator does not
    /// reference.</summary>
    private static bool BuildsATree(INamedTypeSymbol symbol)
    {
        foreach (INamedTypeSymbol i in symbol.AllInterfaces)
        {
            if (i.Name == TreeBuilderInterfaceName
                && i.ContainingNamespace?.ToDisplayString() == TreeBuilderInterfaceNamespace)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Is this type an ECS component? Two tests, because neither alone is enough: one declared
    /// HERE gets its <c>: IComponent</c> from the ECS generator, which this generator cannot see,
    /// so it is recognised by the [Component] attribute; one from a REFERENCED assembly is already
    /// compiled, so there the interface is real metadata.
    /// </summary>
    private static bool IsComponent(ITypeSymbol type)
    {
        foreach (AttributeData attr in type.GetAttributes())
        {
            INamedTypeSymbol? ac = attr.AttributeClass;
            if (ac?.Name == "ComponentAttribute"
                && ac.ContainingNamespace?.ToDisplayString() == "Paradise.ECS")
            {
                return true;
            }
        }

        return Implements(type, ComponentInterface);
    }

    /// <summary>
    /// The node a builder class wraps, or null if this is not one. Builders derive from
    /// <c>LeafNode&lt;T&gt;</c> and friends, so the node survives as a generic argument on the base
    /// — which a factory returning a bare definition does not.
    /// </summary>
    private static INamedTypeSymbol? BuiltNodeOf(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType
                && current.TypeArguments.Length == 1
                && current.ContainingNamespace?.ToDisplayString() == "Paradise.BT.Builder"
                && current.TypeArguments[0] is INamedTypeSymbol node
                && Implements(node, NodeDataInterface))
            {
                return node;
            }
        }

        return null;
    }

    private static bool Implements(ITypeSymbol type, string interfaceFullName)
    {
        foreach (INamedTypeSymbol i in type.AllInterfaces)
        {
            if (i.ToDisplayString() == interfaceFullName)
            {
                return true;
            }
        }

        return false;
    }

    // ===================== verification + emit =====================

    private static void Emit(
        SourceProductionContext spc,
        ImmutableArray<BindingModel> bindings,
        ImmutableArray<BuilderModel> builders)
    {
        foreach (BindingModel binding in bindings)
        {
            // Union the access of every node in the tree. A type both read and written is a write:
            // the stricter claim is the one that has to hold.
            var merged = new Dictionary<string, Access>(System.StringComparer.Ordinal);
            bool refused = false;

            // A name the tree used that resolved to nothing is very likely a builder generated
            // for a node declared here; recover its access from the table.
            var resolvedAccess = binding.Access.ToBuilder();
            foreach (string name in binding.Unresolved)
            {
                foreach (BuilderModel builder in builders)
                {
                    if (builder.Name == name)
                    {
                        resolvedAccess.AddRange(builder.Access);
                        break;
                    }
                }
            }

            foreach (Access a in resolvedAccess)
            {
                if (a.Kind == AccessKind.OptionalRead)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        s_optionalUnsupported, binding.Location, a.DeclaringNode, a.TypeName));
                    refused = true;
                    continue;
                }

                // A component binds read-only by value — there is no claim to write through, and
                // the union IS the contract, so the one rule left is that a write cannot land.
                if (a.IsComponent && a.Kind == AccessKind.Write)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        s_componentWriteUnsupported, binding.Location, a.DeclaringNode, a.TypeName));
                    refused = true;
                    continue;
                }

                if (!merged.TryGetValue(a.TypeFqn, out Access existing)
                    || (existing.Kind == AccessKind.Read && a.Kind == AccessKind.Write))
                {
                    merged[a.TypeFqn] = a;
                }
            }

            if (refused)
            {
                continue;
            }

            ImmutableArray<Access> access = merged.Values
                .OrderBy(a => a.TypeName, System.StringComparer.Ordinal)
                .ThenBy(a => a.TypeFqn, System.StringComparer.Ordinal)
                .ToImmutableArray();

            // Namespace-qualified for the same reason as BTreeNodeGenerator's hints: a duplicate
            // hint name throws inside Roslyn and drops every binding in the compilation.
            string hint = binding.Namespace.Length == 0
                ? $"{binding.ClassName}.Binding.g.cs"
                : $"{binding.Namespace.Replace('.', '_')}_{binding.ClassName}.Binding.g.cs";
            spc.AddSource(hint, Render(binding, access));
        }
    }

    private static string Render(BindingModel binding, ImmutableArray<Access> access)
    {
        Dictionary<string, string> identifiers = NameIdentifiers(access);
        string FieldOf(Access a) => "_" + identifiers[a.TypeFqn];
        string ParamOf(Access a)
        {
            string name = identifiers[a.TypeFqn];

            // A data type named `Event` yields the parameter `event` — escape any keyword.
            return Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name)
                != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
                ? "@" + name
                : name;
        }

        string bb = binding.ClassName + "Blackboard";
        string extras = binding.ClassName + "Extras";
        // What flows IN is a parameter; what comes BACK is Extras. A component is always an input,
        // and so is a non-component the tree only reads — delta time, a sensed value the caller
        // computed. Only what a node WRITES needs somewhere the caller can read it from, which is
        // the one thing a by-value parameter cannot be.
        ImmutableArray<Access> inputs = access
            .Where(a => a.IsComponent || a.Kind != AccessKind.Write)
            .ToImmutableArray();
        ImmutableArray<Access> outputs = access
            .Where(a => !a.IsComponent && a.Kind == AccessKind.Write)
            .ToImmutableArray();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (binding.Namespace.Length > 0)
        {
            sb.AppendLine("namespace " + binding.Namespace + ";");
            sb.AppendLine();
        }

        // ----- blackboard -----
        sb.AppendLine("/// <summary>" + binding.ClassName + "'s blackboard — the union of its nodes' access.");
        sb.AppendLine("///");
        sb.AppendLine("/// Holds a REFERENCE to everything it touches: what the tree reads by");
        sb.AppendLine("/// <c>ref readonly</c>, what it writes by <c>ref</c>, so a write lands in the caller's own");
        sb.AppendLine("/// storage — a component in chunk memory, a conclusion in a local. Nothing is copied and");
        sb.AppendLine("/// there is nothing to read back out of.");
        sb.AppendLine("///");
        sb.AppendLine("/// A ref struct, therefore, which is why the virtual machine takes a blackboard BY VALUE.");
        sb.AppendLine("/// Passed by <c>ref</c> instead, this would be unusable: CS8350/CS8352 reject the");
        sb.AppendLine("/// combination of two by-ref arguments whose contents could capture each other.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public readonly ref struct " + bb + " : global::Paradise.BT.IBlackboard");
        sb.AppendLine("{");
        foreach (Access a in access)
        {
            string modifier = a.Kind == AccessKind.Write ? "ref" : "ref readonly";
            sb.AppendLine("    private readonly " + modifier + " global::" + a.TypeFqn + " " + FieldOf(a) + ";");
        }

        if (access.Length == 0)
        {
            sb.AppendLine("    // This tree touches nothing.");
        }

        sb.AppendLine();

        var ctorParams = access.Select(a =>
            (a.Kind == AccessKind.Write ? "ref global::" : "in global::") + a.TypeFqn + " " + ParamOf(a));
        sb.AppendLine("    public " + bb + "(" + string.Join(", ", ctorParams) + ")");
        sb.AppendLine("    {");
        foreach (Access a in access)
        {
            sb.AppendLine("        " + FieldOf(a) + " = ref " + ParamOf(a) + ";");
        }

        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Wire one row in. Pass arguments BY NAME: they are ordered by type name, so adding");
        sb.AppendLine("    /// a node can reorder them, and two of the same type would transpose in silence.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static " + bb + " Bind(" + string.Join(", ", ctorParams) + ")");
        sb.AppendLine("        => new(" + string.Join(
            ", ",
            access.Select(a => (a.Kind == AccessKind.Write ? "ref " : "in ") + ParamOf(a))) + ");");
        sb.AppendLine();

        // HasData
        sb.AppendLine("    public bool HasData<T>() where T : struct");
        if (access.Length == 0)
        {
            sb.AppendLine("        => false;");
        }
        else
        {
            sb.AppendLine("        => " + string.Join(
                "\n        || ",
                access.Select(a => "typeof(T) == typeof(global::" + a.TypeFqn + ")")) + ";");
        }

        sb.AppendLine();

        // GetData
        sb.AppendLine("    public T GetData<T>() where T : struct");
        sb.AppendLine("    {");
        foreach (Access a in access)
        {
            sb.AppendLine("        if (typeof(T) == typeof(global::" + a.TypeFqn + "))");
            sb.AppendLine("        {");
            sb.AppendLine("            global::" + a.TypeFqn + " value = " + FieldOf(a) + ";");
            sb.AppendLine("            return global::System.Runtime.CompilerServices.Unsafe"
                + ".As<global::" + a.TypeFqn + ", T>(ref value);");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        throw Missing<T>();");
        sb.AppendLine("    }");
        sb.AppendLine();

        // SetData
        sb.AppendLine("    public void SetData<T>(T value) where T : struct");
        sb.AppendLine("    {");
        foreach (Access a in access.Where(a => a.Kind == AccessKind.Write))
        {
            sb.AppendLine("        if (typeof(T) == typeof(global::" + a.TypeFqn + "))");
            sb.AppendLine("        {");
            sb.AppendLine("            " + FieldOf(a)
                + " = global::System.Runtime.CompilerServices.Unsafe"
                + ".As<T, global::" + a.TypeFqn + ">(ref value);");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        // Held by `ref readonly`, so there is nowhere for a write to go. PBT0009 refuses a node
        // that performs one and PBT0008 one that declares it, leaving this reachable only through
        // a hand-written blackboard — where silence would be a write that vanishes.
        foreach (Access a in access.Where(a => a.Kind != AccessKind.Write))
        {
            sb.AppendLine("        if (typeof(T) == typeof(global::" + a.TypeFqn + "))");
            sb.AppendLine("        {");
            sb.AppendLine("            throw new global::System.InvalidOperationException(");
            sb.AppendLine("                \"'" + a.TypeName + "' is bound read-only: no node declares [Writes<\"");
            sb.AppendLine("                + \"" + a.TypeName + ">], so the caller passed it by `in`.\");");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        throw Missing<T>();");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    private static global::System.Collections.Generic.KeyNotFoundException Missing<T>()");
        sb.AppendLine("        => new(");
        sb.AppendLine("            $\"" + bb + " carries no '{typeof(T).FullName}'. It carries exactly what the \"");
        sb.AppendLine("            + \"nodes in " + binding.ClassName
            + " declare with [Reads<T>] / [Writes<T>]; declare it there.\");");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// One identifier per access entry, keyed by FQN. Simple names are the readable default;
    /// two same-named types from different namespaces both survive the FQN-keyed merge, so a
    /// colliding GROUP is suffixed with each member's namespace — emitting two `_target` fields
    /// would be CS0102 in a file the user cannot edit.
    /// </summary>
    private static Dictionary<string, string> NameIdentifiers(ImmutableArray<Access> access)
    {
        var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var group in access.GroupBy(
            a => char.ToLowerInvariant(a.TypeName[0]) + a.TypeName.Substring(1),
            System.StringComparer.Ordinal))
        {
            var members = group.ToList();
            if (members.Count == 1)
            {
                map[members[0].TypeFqn] = group.Key;
                continue;
            }

            foreach (Access a in members)
            {
                string ns = a.TypeFqn.Length > a.TypeName.Length + 1
                    ? a.TypeFqn.Substring(0, a.TypeFqn.Length - a.TypeName.Length - 1).Replace('.', '_')
                    : "global";
                map[a.TypeFqn] = group.Key + "_" + ns;
            }
        }

        return map;
    }

    // ===================== models =====================

    private enum AccessKind
    {
        Read,
        Write,
        OptionalRead,
    }

    // Plain structs with explicit IEquatable, matching BTreeNodeGenerator. Not records: the
    // generator targets netstandard2.0, which has no IsExternalInit. Value equality is also what
    // makes the incremental cache work at all — ImmutableArray compares by REFERENCE by default,
    // so every member holding one has to be compared with SequenceEqual by hand.
    private readonly struct Access : System.IEquatable<Access>
    {
        /// <summary>The node that declared it — carried so a diagnostic can name the culprit.</summary>
        public readonly string DeclaringNode;
        public readonly string TypeFqn;
        public readonly string TypeName;
        public readonly AccessKind Kind;
        public readonly bool IsComponent;

        public Access(
            string declaringNode, string typeFqn, string typeName, AccessKind kind, bool isComponent)
        {
            DeclaringNode = declaringNode;
            TypeFqn = typeFqn;
            TypeName = typeName;
            Kind = kind;
            IsComponent = isComponent;
        }

        public bool Equals(Access other) =>
            DeclaringNode == other.DeclaringNode
            && TypeFqn == other.TypeFqn
            && TypeName == other.TypeName
            && Kind == other.Kind
            && IsComponent == other.IsComponent;

        public override bool Equals(object? obj) => obj is Access other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = TypeFqn.GetHashCode();
                hash = (hash * 31) + (int)Kind;
                hash = (hash * 31) + (IsComponent ? 1 : 0);
                return hash;
            }
        }
    }

    private readonly struct BuilderModel : System.IEquatable<BuilderModel>
    {
        public readonly string Name;
        public readonly ImmutableArray<Access> Access;

        public BuilderModel(string name, ImmutableArray<Access> access)
        {
            Name = name;
            Access = access;
        }

        public bool Equals(BuilderModel other) =>
            Name == other.Name && Access.SequenceEqual(other.Access);

        public override bool Equals(object? obj) => obj is BuilderModel other && Equals(other);

        public override int GetHashCode() => Name.GetHashCode();
    }

    private readonly struct BindingModel : System.IEquatable<BindingModel>
    {
        public readonly string Namespace;
        public readonly string ClassName;

        /// <summary>Every access every node in this tree declares, already resolved. Flattened
        /// rather than grouped per node: nothing downstream needs the grouping, and each entry
        /// remembers its own <see cref="Access.DeclaringNode"/>.</summary>
        public readonly ImmutableArray<Access> Access;

        /// <summary>Names the tree used that resolved to nothing — most often a builder generated
        /// for a node declared here, which this generator cannot see.</summary>
        public readonly ImmutableArray<string> Unresolved;
        public readonly Location Location;

        public BindingModel(
            string ns,
            string className,
            ImmutableArray<Access> access,
            ImmutableArray<string> unresolved,
            Location location)
        {
            Namespace = ns;
            ClassName = className;
            Access = access;
            Unresolved = unresolved;
            Location = location;
        }

        public bool Equals(BindingModel other) =>
            Namespace == other.Namespace
            && ClassName == other.ClassName
            && Access.SequenceEqual(other.Access)
            && Unresolved.SequenceEqual(other.Unresolved);

        public override bool Equals(object? obj) => obj is BindingModel other && Equals(other);

        public override int GetHashCode() => ClassName.GetHashCode();
    }
}
