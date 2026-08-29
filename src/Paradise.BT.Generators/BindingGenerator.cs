using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Paradise.BT.Generators;

/// <summary>
/// Binds a behavior tree to the ECS rows that feed it.
///
/// Scans each <c>[BehaviorTreeBinding(typeof(TQueryable))]</c> class for the node types it
/// mentions, unions their declared access, checks it against the queryable's claims, and emits a
/// blackboard over those components plus a <c>Bind</c> that wires one row into it.
///
/// It VERIFIES a queryable; it never authors one. That is forced, not chosen: a generator cannot
/// see another generator's output in the same compilation — Paradise.ECS says so in its own
/// SystemGenerator ("QueryableGenerator output isn't visible to SystemGenerator") — so an emitted
/// <c>[Queryable]</c> would be invisible to the generator that has to expand it. Both generators
/// therefore read the same hand-written attributes.
///
/// No reference to Paradise.ECS is taken, and none is possible: Paradise.BT does not depend on it.
/// A type counts as a component when it implements an interface NAMED Paradise.ECS.IComponent,
/// tested symbolically. Everything else is a caller-supplied extra.
/// </summary>
[Generator]
public sealed class BindingGenerator : IIncrementalGenerator
{
    private const string BindingAttributeFullName = "Paradise.BT.BehaviorTreeBindingAttribute";
    private const string QueryableAttributeFullName = "Paradise.ECS.QueryableAttribute";
    private const string NodeDataInterface = "Paradise.BT.INodeData";
    private const string ComponentInterface = "Paradise.ECS.IComponent";
    private const string BlackboardInterface = "Paradise.BT.IBlackboard";

    private static readonly DiagnosticDescriptor s_readsUnclaimed = new(
        id: "PBT0005",
        title: "Node reads a component the queryable does not claim",
        messageFormat: "Node '{0}' declares [Reads<{1}>], but queryable '{2}' {3}. Add [With<{1}>(IsReadOnly = true)] to it.",
        category: "Paradise.BT.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_optionalUnsupported = new(
        id: "PBT0006",
        title: "Optional component access is not supported",
        messageFormat: "Node '{0}' declares [OptionalReads<{1}>], which is not supported yet: the ECS emits optional accessors on a queryable's per-entity view only, never on the Segments view a world system iterates. Use [Reads<{1}>] and a required claim.",
        category: "Paradise.BT.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_componentWriteUnsupported = new(
        id: "PBT0008",
        title: "Writing a component from a node is not supported",
        messageFormat: "Node '{0}' declares [Writes<{1}>], and {1} is an ECS component. A blackboard holding a writable reference into chunk memory cannot be passed to the virtual machine under the current ref-safety rules, so components are bound by value and are read-only. Have the node write a non-component conclusion the system applies instead.",
        category: "Paradise.BT.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_notQueryable = new(
        id: "PBT0007",
        title: "Binding target is not a queryable",
        messageFormat: "'{0}' names '{1}', which carries no [Queryable] attribute. Its claims cannot be checked, so no binding was emitted.",
        category: "Paradise.BT.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var queryables = context.SyntaxProvider.ForAttributeWithMetadataName(
                QueryableAttributeFullName,
                predicate: static (node, _) => node is StructDeclarationSyntax,
                transform: static (ctx, _) => GetQueryable(ctx))
            .Where(static q => q.HasValue)
            .Select(static (q, _) => q!.Value);

        var bindings = context.SyntaxProvider.ForAttributeWithMetadataName(
                BindingAttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => GetBinding(ctx, ct))
            .Where(static b => b.HasValue)
            .Select(static (b, _) => b!.Value);

        context.RegisterSourceOutput(
            bindings.Collect().Combine(queryables.Collect()),
            static (spc, data) => Emit(spc, data.Left, data.Right));
    }

    // ===================== collection =====================

    private static QueryableModel? GetQueryable(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return null;
        }

        var claims = ImmutableArray.CreateBuilder<Claim>();
        foreach (var attr in symbol.GetAttributes())
        {
            INamedTypeSymbol? ac = attr.AttributeClass;
            if (ac is null
                || !ac.IsGenericType
                || ac.Name != "WithAttribute"
                || ac.ContainingNamespace?.ToDisplayString() != "Paradise.ECS")
            {
                continue;
            }

            bool isReadOnly = false;
            bool queryOnly = false;
            string? nameOverride = null;
            foreach (var named in attr.NamedArguments)
            {
                switch (named.Key)
                {
                    case "IsReadOnly":
                        isReadOnly = named.Value.Value is true;
                        break;
                    case "QueryOnly":
                        queryOnly = named.Value.Value is true;
                        break;
                    case "Name":
                        nameOverride = named.Value.Value as string;
                        break;
                }
            }

            ITypeSymbol component = ac.TypeArguments[0];
            claims.Add(new Claim(
                component.ToDisplayString(),
                nameOverride ?? component.Name,
                isReadOnly,
                queryOnly));
        }

        return new QueryableModel(symbol.ToDisplayString(), claims.ToImmutable());
    }

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

        return access.ToImmutable();
    }

    /// <summary>
    /// Read a node's access out of its BODY: every <c>bb.GetData&lt;T&gt;()</c> is a read, every
    /// <c>bb.SetData&lt;T&gt;()</c> a write. Only possible since those two replaced the
    /// ref-returning accessor — taking a ref to avoid a copy and taking one to mutate looked
    /// identical, so no scan could tell them apart.
    ///
    /// This runs only where the body EXISTS, which is the whole rule: a node declared in this
    /// compilation is scanned and needs no attributes, while a node arriving from a referenced
    /// assembly has no syntax at all and is read from its attributes, which is why those remain
    /// the cross-assembly contract. <c>DeclaringSyntaxReferences</c> is exactly that test.
    ///
    /// Unioned with whatever the node declares rather than replacing it, so a node reaching the
    /// blackboard somewhere this cannot follow — through a helper, see PBT0010 — can still say so
    /// by hand.
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
        GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ctx.TargetSymbol is not INamedTypeSymbol symbol
            || ctx.TargetNode is not ClassDeclarationSyntax decl)
        {
            return null;
        }

        AttributeData attr = ctx.Attributes[0];
        if (attr.ConstructorArguments.Length != 1
            || attr.ConstructorArguments[0].Value is not INamedTypeSymbol queryable)
        {
            return null;
        }

        // Every node type the class MENTIONS, in any spelling: `new StrikeNode()`, `Node<T>(...)`,
        // a `typeof`. All of them surface as a TypeSyntax, so one sweep catches each. Deliberately
        // an over-approximation: a stray mention costs a loud PBT0004/5, never a silent miss.
        var access = ImmutableArray.CreateBuilder<Access>();
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

            if (resolved is not INamedTypeSymbol t)
            {
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

        // The escape hatch: a factory nobody has annotated.
        foreach (var named in attr.NamedArguments)
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

        string ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();

        return new BindingModel(
            ns,
            symbol.Name,
            queryable.ToDisplayString(),
            access.ToImmutable(),
            decl.Identifier.GetLocation());
    }

    /// <summary>
    /// Is this type an ECS component?
    ///
    /// Two tests, because neither alone is enough. A component declared in THIS compilation gets
    /// its <c>: IComponent</c> from Paradise.ECS's own ComponentGenerator — generator output,
    /// which this generator cannot see, exactly as SystemGenerator cannot see QueryableGenerator's.
    /// So source-declared components are recognised by the <c>[Component]</c> ATTRIBUTE on the
    /// original declaration, the one thing both generators can read. A component arriving from a
    /// REFERENCED assembly is already compiled, so there the interface is real metadata and the
    /// attribute may have been on a partial this compilation never sees.
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
    /// The node a builder class wraps, or null if this is not one.
    ///
    /// The generated builders derive from <c>LeafNode&lt;T&gt;</c> / <c>DecoratorNode&lt;T&gt;</c> /
    /// <c>CompositeNode&lt;T&gt;</c>, so the node type survives as a generic argument on the base.
    /// That is the difference between the DSL and a factory method: a method returning
    /// <c>BehaviorNodeDefinition</c> throws the type away and has to be told with
    /// <c>[Builds&lt;T&gt;]</c>; a builder still carries it.
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
        ImmutableArray<QueryableModel> queryables)
    {
        foreach (BindingModel binding in bindings)
        {
            QueryableModel queryable = default;
            bool found = false;
            foreach (QueryableModel q in queryables)
            {
                if (q.Fqn == binding.QueryableFqn)
                {
                    queryable = q;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    s_notQueryable, binding.Location, binding.ClassName, binding.QueryableFqn));
                continue;
            }

            // Union the access of every node in the tree. A type both read and written is a write:
            // the stricter claim is the one that has to hold.
            var merged = new Dictionary<string, Access>(System.StringComparer.Ordinal);
            bool refused = false;

            foreach (Access a in binding.Access)
            {
                if (a.Kind == AccessKind.OptionalRead)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        s_optionalUnsupported, binding.Location, a.DeclaringNode, a.TypeName));
                    refused = true;
                    continue;
                }

                if (a.IsComponent && a.Kind == AccessKind.Write)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        s_componentWriteUnsupported, binding.Location, a.DeclaringNode, a.TypeName));
                    refused = true;
                    continue;
                }

                if (a.IsComponent && !Verify(spc, binding, queryable, a))
                {
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
                .ToImmutableArray();

            spc.AddSource($"{binding.ClassName}.Binding.g.cs", Render(binding, queryable, access));
        }
    }

    private static bool Verify(
        SourceProductionContext spc,
        BindingModel binding,
        QueryableModel queryable,
        Access access)
    {
        Claim claim = default;
        bool claimed = false;
        foreach (Claim c in queryable.Claims)
        {
            if (c.ComponentFqn == access.TypeFqn)
            {
                claim = c;
                claimed = true;
                break;
            }
        }

        string? problem =
            !claimed ? "does not claim it"
            : claim.QueryOnly ? "claims it QueryOnly, which generates no accessor"
            : null;

        if (problem is null)
        {
            return true;
        }

        spc.ReportDiagnostic(Diagnostic.Create(
            s_readsUnclaimed,
            binding.Location,
            access.DeclaringNode,
            access.TypeName,
            queryable.Fqn,
            problem));
        return false;
    }

    private static string Render(
        BindingModel binding, QueryableModel queryable, ImmutableArray<Access> access)
    {
        string bb = binding.ClassName + "Blackboard";
        string extras = binding.ClassName + "Extras";
        ImmutableArray<Access> components = access.Where(a => a.IsComponent).ToImmutableArray();
        ImmutableArray<Access> plain = access.Where(a => !a.IsComponent).ToImmutableArray();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (binding.Namespace.Length > 0)
        {
            sb.AppendLine("namespace " + binding.Namespace + ";");
            sb.AppendLine();
        }

        // ----- extras -----
        sb.AppendLine("/// <summary>What " + binding.ClassName + "'s tree reads that is NOT a component:");
        sb.AppendLine("/// derived values, and the tree's own conclusions. The caller fills it.</summary>");
        sb.AppendLine("public struct " + extras);
        sb.AppendLine("{");
        foreach (Access a in plain)
        {
            sb.AppendLine("    public global::" + a.TypeFqn + " " + a.TypeName + ";");
        }

        if (plain.Length == 0)
        {
            sb.AppendLine("    // This tree reads only components.");
        }

        sb.AppendLine("}");
        sb.AppendLine();

        // ----- blackboard -----
        sb.AppendLine("/// <summary>" + binding.ClassName + "'s blackboard, over one " + queryable.Fqn + " row.");
        sb.AppendLine("///");
        sb.AppendLine("/// Components are copied in BY VALUE and are read-only; what the tree concludes goes");
        sb.AppendLine("/// into Extras, which the caller reads back. An ordinary struct, not a ref struct:");
        sb.AppendLine("/// holding refs or spans into chunk memory makes it unpassable to VirtualMachine.Tick");
        sb.AppendLine("/// (CS8350/CS8352), and with only value fields there is nothing a ref struct would buy.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public struct " + bb + " : global::Paradise.BT.IBlackboard");
        sb.AppendLine("{");
        // Components are copied in BY VALUE. A blackboard holding refs or spans into chunk
        // memory cannot be passed as `ref` to VirtualMachine.Tick (CS8350/CS8352) — ref fields,
        // one-element spans, call-site resolution, and `scoped` on both parameters and the local
        // were all tried and none help, because `TBlackboard allows ref struct` makes the compiler
        // assume the worst about an unsubstituted type parameter. A copy has no scope to reason
        // about. The cost is a memcpy per component per tick; the limit is that a node cannot
        // WRITE a component (PBT0008) until that is solved.
        foreach (Access a in components)
        {
            sb.AppendLine("    private readonly global::" + a.TypeFqn + " " + Field(a) + ";");
        }

        sb.AppendLine("    /// <summary>The non-component data, held BY VALUE. The caller reads back what the");
        sb.AppendLine("    /// tree wrote from here.</summary>");
        sb.AppendLine("    public " + extras + " Extras;");
        sb.AppendLine();

        // One ref per component, taken from the segment indexer — the same shape
        // BehaviorTreeState.BlobOf uses to hand a chunk ref to UnmanagedNodeBlob.
        var ctorParams = components
            .Select(a => "in global::" + a.TypeFqn + " " + Param(a))
            .Concat(new[] { "in " + extras + " extras" });
        sb.AppendLine("    public " + bb + "(" + string.Join(", ", ctorParams) + ")");
        sb.AppendLine("    {");
        foreach (Access a in components)
        {
            sb.AppendLine("        " + Field(a) + " = " + Param(a) + ";");
        }

        sb.AppendLine("        Extras = extras;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Bind takes the component REFS, resolved by the caller off the segment indexer — it does
        // NOT take the Segments and index them here. That is load-bearing, not a style choice.
        // ComponentSegments' indexer returns a ref reached through its ChunkManager, which is a
        // CLASS, so `rows.X[i]` at the call site is a ref of unrestricted scope. Resolving it
        // inside this method instead ties it to the `rows` parameter — a by-value ref struct,
        // whose scope is the calling method — and the blackboard built from it is then too narrow
        // to pass as `ref` to VirtualMachine.Tick (CS8350/CS8352).
        //
        // BehaviorTreeState.BlobOf is the same shape and the reason it was always fine:
        // `BlobOf(ref Pack.BehaviorTreeState[i], layout)`.
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Wire one row's components in. Resolve them at the CALL SITE:");
        sb.AppendLine("    /// <code>");
        sb.Append("    /// var bb = " + bb + ".Bind(");
        sb.AppendLine(string.Join(
            ", ",
            components.Select(a =>
                "in rows." + PropertyOf(queryable, a) + "[i]")
                .Concat(new[] { "in extras" })));
        sb.AppendLine("    /// </code>");
        sb.AppendLine("    /// Passing the segments and indexing them in here instead would narrow every ref to");
        sb.AppendLine("    /// this method and make the result unpassable to the VM.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static " + bb + " Bind(" + string.Join(", ", ctorParams) + ")");
        sb.AppendLine("        => new(" + string.Join(
            ", ",
            components.Select(a => "in " + Param(a))
                .Concat(new[] { "in extras" })) + ");");
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
        foreach (Access a in components)
        {
            sb.AppendLine("        if (typeof(T) == typeof(global::" + a.TypeFqn + "))");
            sb.AppendLine("        {");
            sb.AppendLine("            global::" + a.TypeFqn + " value = " + Field(a) + ";");
            sb.AppendLine("            return global::System.Runtime.CompilerServices.Unsafe"
                + ".As<global::" + a.TypeFqn + ", T>(ref value);");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        foreach (Access a in plain)
        {
            sb.AppendLine("        if (typeof(T) == typeof(global::" + a.TypeFqn + "))");
            sb.AppendLine("        {");
            sb.AppendLine("            return global::System.Runtime.CompilerServices.Unsafe"
                + ".As<global::" + a.TypeFqn + ", T>(ref Extras." + a.TypeName + ");");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        throw Missing<T>();");
        sb.AppendLine("    }");
        sb.AppendLine();

        // SetData
        sb.AppendLine("    public void SetData<T>(T value) where T : struct");
        sb.AppendLine("    {");
        foreach (Access a in plain)
        {
            sb.AppendLine("        if (typeof(T) == typeof(global::" + a.TypeFqn + "))");
            sb.AppendLine("        {");
            sb.AppendLine("            Extras." + a.TypeName
                + " = global::System.Runtime.CompilerServices.Unsafe"
                + ".As<T, global::" + a.TypeFqn + ">(ref value);");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        // Components are bound by value, so a write could not reach the chunk. PBT0008 refuses a
        // node that DECLARES the write and PBT0009 one that merely performs it, so this is only
        // reachable through a hand-written blackboard or reflection — but silence here would be a
        // write that vanishes.
        foreach (Access a in components)
        {
            sb.AppendLine("        if (typeof(T) == typeof(global::" + a.TypeFqn + "))");
            sb.AppendLine("        {");
            sb.AppendLine("            throw new global::System.InvalidOperationException(");
            sb.AppendLine("                \"'" + a.TypeName + "' is a component, bound BY VALUE, so writing it here \"");
            sb.AppendLine("                + \"would not reach the chunk. Read it with GetData<" + a.TypeName + ">(), \"");
            sb.AppendLine("                + \"and write a conclusion the system applies.\");");
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

    private static string Field(Access a) =>
        "_" + char.ToLowerInvariant(a.TypeName[0]) + a.TypeName.Substring(1);

    private static string Param(Access a) =>
        char.ToLowerInvariant(a.TypeName[0]) + a.TypeName.Substring(1);

    private static string PropertyOf(QueryableModel queryable, Access a)
    {
        foreach (Claim c in queryable.Claims)
        {
            if (c.ComponentFqn == a.TypeFqn)
            {
                return c.PropertyName;
            }
        }

        return a.TypeName;
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

    private readonly struct Claim : System.IEquatable<Claim>
    {
        public readonly string ComponentFqn;
        public readonly string PropertyName;
        public readonly bool IsReadOnly;
        public readonly bool QueryOnly;

        public Claim(string componentFqn, string propertyName, bool isReadOnly, bool queryOnly)
        {
            ComponentFqn = componentFqn;
            PropertyName = propertyName;
            IsReadOnly = isReadOnly;
            QueryOnly = queryOnly;
        }

        public bool Equals(Claim other) =>
            ComponentFqn == other.ComponentFqn
            && PropertyName == other.PropertyName
            && IsReadOnly == other.IsReadOnly
            && QueryOnly == other.QueryOnly;

        public override bool Equals(object? obj) => obj is Claim other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = ComponentFqn.GetHashCode();
                hash = (hash * 31) + (IsReadOnly ? 1 : 0);
                hash = (hash * 31) + (QueryOnly ? 1 : 0);
                return hash;
            }
        }
    }

    private readonly struct QueryableModel : System.IEquatable<QueryableModel>
    {
        public readonly string Fqn;
        public readonly ImmutableArray<Claim> Claims;

        public QueryableModel(string fqn, ImmutableArray<Claim> claims)
        {
            Fqn = fqn;
            Claims = claims;
        }

        public bool Equals(QueryableModel other) =>
            Fqn == other.Fqn && Claims.SequenceEqual(other.Claims);

        public override bool Equals(object? obj) => obj is QueryableModel other && Equals(other);

        public override int GetHashCode() => Fqn.GetHashCode();
    }

    private readonly struct BindingModel : System.IEquatable<BindingModel>
    {
        public readonly string Namespace;
        public readonly string ClassName;
        public readonly string QueryableFqn;

        /// <summary>Every access every node in this tree declares, already resolved. Flattened
        /// rather than grouped per node: nothing downstream needs the grouping, and each entry
        /// remembers its own <see cref="Access.DeclaringNode"/>.</summary>
        public readonly ImmutableArray<Access> Access;
        public readonly Location Location;

        public BindingModel(
            string ns,
            string className,
            string queryableFqn,
            ImmutableArray<Access> access,
            Location location)
        {
            Namespace = ns;
            ClassName = className;
            QueryableFqn = queryableFqn;
            Access = access;
            Location = location;
        }

        public bool Equals(BindingModel other) =>
            Namespace == other.Namespace
            && ClassName == other.ClassName
            && QueryableFqn == other.QueryableFqn
            && Access.SequenceEqual(other.Access);

        public override bool Equals(object? obj) => obj is BindingModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (ClassName.GetHashCode() * 31) + QueryableFqn.GetHashCode();
            }
        }
    }
}
