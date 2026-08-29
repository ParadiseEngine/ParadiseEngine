using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Paradise.BT.Generators;

[Generator]
public sealed class BTreeNodeGenerator : IIncrementalGenerator
{
    private const string BuilderAttributeFullName = "Paradise.BT.BuilderAttribute";
    private const string GuidAttributeFullName = "System.Runtime.InteropServices.GuidAttribute";

    private static readonly DiagnosticDescriptor s_missingGuidDiagnostic = new(
        id: "PBT0001",
        title: "Missing [Guid] attribute",
        messageFormat: "Struct '{0}' has [Builder] but is missing [Guid] attribute required for serialization",
        category: "Paradise.BT.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor s_notUnmanagedDiagnostic = new(
        id: "PBT0002",
        title: "Builder struct is not unmanaged",
        messageFormat: "Struct '{0}' has [Builder] but contains managed references and cannot be used as an INodeData builder",
        category: "Paradise.BT.Generators",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor s_multipleConstructorsDiagnostic = new(
        id: "PBT0011",
        title: "Ambiguous node constructor",
        messageFormat: "Struct '{0}' has [Builder] and declares more than one public constructor, "
            + "so the generator cannot tell which one is the exposed surface — keep exactly one",
        category: "Paradise.BT.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor s_unexposedPublicFieldDiagnostic = new(
        id: "PBT0012",
        title: "Public field is not exposed by the constructor",
        messageFormat: "Struct '{0}' declares a constructor, which makes the constructor the "
            + "exposed surface — public field '{1}' is not one of its parameters, so the builder "
            + "will not expose it; add it to the constructor or make it private runtime state",
        category: "Paradise.BT.Generators",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) =>
                IsStructDeclaration(node) && ((TypeDeclarationSyntax)node).AttributeLists.Count > 0,
            transform: static (ctx, ct) => GetNodeInfo(ctx, ct)
        ).Where(static info => info.HasValue)
         .Select(static (info, _) => info!.Value);

        // Registration is emitted from a SEPARATE pass over every INodeData struct, not from the
        // [Builder] pass above. The two sets are not the same: DelayTimerNode is registerable and
        // used to have no [Builder] at all, back when a factory built it. Keying registration
        // on [Builder] would silently drop it, and with it every timer node in every tree.
        var registrable = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) =>
                IsStructDeclaration(node) && ((TypeDeclarationSyntax)node).BaseList?.Types.Count > 0,
            transform: static (ctx, ct) => GetRegistrableNode(ctx, ct)
        ).Where(static node => node.HasValue)
         .Select(static (node, _) => node!.Value);

        context.RegisterSourceOutput(
            registrable.Collect(), static (spc, names) => EmitRegistration(spc, names));

        context.RegisterSourceOutput(provider, static (spc, info) =>
        {
            if (!info.HasGuid)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    s_missingGuidDiagnostic,
                    info.Location,
                    info.StructName
                ));
                return;
            }

            if (!info.IsUnmanaged)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    s_notUnmanagedDiagnostic,
                    info.Location,
                    info.StructName
                ));
                return;
            }

            if (info.HasMultipleConstructors)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    s_multipleConstructorsDiagnostic,
                    info.Location,
                    info.StructName
                ));
                return;
            }

            foreach (var fieldName in info.UnexposedPublicFields)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    s_unexposedPublicFieldDiagnostic,
                    info.Location,
                    info.StructName,
                    fieldName
                ));
            }

            var source = GenerateWrapper(info);
            spc.AddSource($"{info.GeneratedClassName}.g.cs", source);
        });
    }

    private static NodeInfo? GetNodeInfo(GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var structDecl = (TypeDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(structDecl, ct) is not INamedTypeSymbol structSymbol)
            return null;

        ct.ThrowIfCancellationRequested();

        // Get [Builder] attribute data
        AttributeData? builderAttr = null;
        foreach (var attr in structSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == BuilderAttributeFullName)
            {
                builderAttr = attr;
                break;
            }
        }
        if (builderAttr is null)
            return null;

        // Parse attribute arguments
        string? nameOverride = null;
        int cardinality = 0; // Leaf

        if (builderAttr.ConstructorArguments.Length == 1)
        {
            var arg = builderAttr.ConstructorArguments[0];
            if (arg.Type?.SpecialType == SpecialType.System_String)
            {
                nameOverride = arg.Value as string;
            }
            else
            {
                cardinality = (int)(arg.Value ?? 0);
            }
        }
        else if (builderAttr.ConstructorArguments.Length == 2)
        {
            nameOverride = builderAttr.ConstructorArguments[0].Value as string;
            cardinality = (int)(builderAttr.ConstructorArguments[1].Value ?? 0);
        }

        // Check for [Guid]
        bool hasGuid = false;
        foreach (var attr in structSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == GuidAttributeFullName)
            {
                hasGuid = true;
                break;
            }
        }

        // Check if unmanaged
        bool isUnmanaged = structSymbol.IsUnmanagedType;

        // Determine generated class name
        string structName = structSymbol.Name;
        string generatedName = nameOverride ?? StripNodeSuffix(structName);

        // Get namespace
        string? ns = structSymbol.ContainingNamespace?.IsGlobalNamespace == true
            ? null
            : structSymbol.ContainingNamespace?.ToDisplayString();

        // The exposed surface: the declared constructor's parameters when there is one
        // (everything else is runtime state the builder never shows), otherwise every public
        // value field.
        var publicCtors = ImmutableArray.CreateBuilder<IMethodSymbol>();
        foreach (var ctorSymbol in structSymbol.InstanceConstructors)
        {
            if (!ctorSymbol.IsImplicitlyDeclared
                && ctorSymbol.DeclaredAccessibility == Accessibility.Public)
            {
                publicCtors.Add(ctorSymbol);
            }
        }

        var publicFields = ImmutableArray.CreateBuilder<IFieldSymbol>();
        foreach (var member in structSymbol.GetMembers())
        {
            if (member is IFieldSymbol field
                && field.DeclaredAccessibility == Accessibility.Public
                && !field.IsStatic
                && !field.IsConst
                && field.Type.IsValueType)
            {
                publicFields.Add(field);
            }
        }

        var fieldsBuilder = ImmutableArray.CreateBuilder<FieldInfo>();
        var unexposed = ImmutableArray<string>.Empty;
        bool useConstructor = publicCtors.Count == 1;
        if (useConstructor)
        {
            foreach (var parameter in publicCtors[0].Parameters)
            {
                fieldsBuilder.Add(new FieldInfo(
                    parameter.Name,
                    parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    RenderDefault(parameter)
                ));
            }

            var unexposedBuilder = ImmutableArray.CreateBuilder<string>();
            foreach (var field in publicFields)
            {
                bool matchesParameter = false;
                foreach (var parameter in publicCtors[0].Parameters)
                {
                    if (string.Equals(field.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        matchesParameter = true;
                        break;
                    }
                }

                if (!matchesParameter)
                {
                    unexposedBuilder.Add(field.Name);
                }
            }

            unexposed = unexposedBuilder.ToImmutable();
        }
        else
        {
            // No constructor: exposed = every public value field. First required, rest optional
            // for leaves and decorators; all required for composites, whose `params children`
            // must come last.
            for (int i = 0; i < publicFields.Count; i++)
            {
                IFieldSymbol field = publicFields[i];
                fieldsBuilder.Add(new FieldInfo(
                    field.Name,
                    field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    i == 0 || cardinality == 2 ? null : "default"
                ));
            }
        }

        var fields = fieldsBuilder.ToImmutable();

        string fullyQualifiedName = structSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return new NodeInfo(
            structName,
            fullyQualifiedName,
            generatedName,
            ns,
            cardinality,
            hasGuid,
            isUnmanaged,
            useConstructor,
            publicCtors.Count > 1,
            unexposed,
            fields,
            structDecl.GetLocation()
        );
    }

    /// <summary>
    /// A parameter's default re-rendered from its constant VALUE, not its expression text —
    /// `NodeState.Running` in the node's file may not resolve in the generated one. Null means
    /// required.
    /// </summary>
    private static string? RenderDefault(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue)
        {
            return null;
        }

        object? value = parameter.ExplicitDefaultValue;
        if (value is null)
        {
            return "default";
        }

        if (parameter.Type.TypeKind == TypeKind.Enum)
        {
            string enumType = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return $"({enumType})({System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)})";
        }

        return value switch
        {
            bool b => b ? "true" : "false",
            float f when !float.IsNaN(f) && !float.IsInfinity(f) =>
                f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
            double d when !double.IsNaN(d) && !double.IsInfinity(d) =>
                d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d",
            decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m",
            string s => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(s, quote: true),
            char c => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(c, quote: true),
            byte or sbyte or short or ushort or int =>
                System.Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
            uint u => u.ToString(System.Globalization.CultureInfo.InvariantCulture) + "u",
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L",
            ulong ul => ul.ToString(System.Globalization.CultureInfo.InvariantCulture) + "UL",
            _ => "default",
        };
    }

    /// <summary>A struct-kind declaration: `struct` or `record struct` — the latter is a
    /// <see cref="RecordDeclarationSyntax"/>, which a `StructDeclarationSyntax` pattern silently
    /// drops.</summary>
    internal static bool IsStructDeclaration(SyntaxNode node) =>
        node is StructDeclarationSyntax || node.IsKind(SyntaxKind.RecordStructDeclaration);

    private const string NodeDataFullName = "Paradise.BT.INodeData";

    /// <summary>
    /// The fully-qualified name of a node type that can be registered, or null.
    ///
    /// Three conditions, and each drops a real case: it must implement INodeData, it must be
    /// unmanaged (a node holding a reference cannot be stored as bytes), and it must carry a
    /// [Guid] (the identity a layout resolves through). Generic and inaccessible types are
    /// skipped too — the emitted initializer is an ordinary internal class, so it can only name
    /// what an internal class can name. That last rule is what keeps a private test node, declared
    /// to prove a layout REFUSES unregistered types, from being registered behind its own back.
    /// </summary>
    private static RegistrableNode? GetRegistrableNode(
        GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ctx.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)ctx.Node, ct)
            is not INamedTypeSymbol symbol)
        {
            return null;
        }

        if (symbol.IsGenericType || !symbol.IsUnmanagedType)
        {
            return null;
        }

        var implementsNodeData = false;
        foreach (var iface in symbol.AllInterfaces)
        {
            if (iface.ToDisplayString() == NodeDataFullName)
            {
                implementsNodeData = true;
                break;
            }
        }

        if (!implementsNodeData || !HasGuidAttribute(symbol) || !IsReachable(symbol))
        {
            return null;
        }

        CollectNodeAccess(
            symbol, ctx.SemanticModel.Compilation,
            out ImmutableArray<string> reads, out ImmutableArray<string> writes, ct);
        return new RegistrableNode(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), reads, writes);
    }

    /// <summary>
    /// What this node touches, for the assembly-level metadata: its <c>Tick</c> body's
    /// <c>GetData</c>/<c>SetData</c> calls unioned with any hand-written access attributes. This
    /// is what a CONSUMING assembly's binding reads, since bodies do not survive into metadata.
    /// </summary>
    private static void CollectNodeAccess(
        INamedTypeSymbol symbol,
        Compilation compilation,
        out ImmutableArray<string> reads,
        out ImmutableArray<string> writes,
        System.Threading.CancellationToken ct)
    {
        var readSet = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
        var writeSet = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            var declaration = reference.GetSyntax(ct);
            var model = compilation.GetSemanticModel(declaration.SyntaxTree);

            foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol target
                    || target.ContainingType?.ToDisplayString() != "Paradise.BT.IBlackboard"
                    || target.TypeArguments.Length != 1)
                {
                    continue;
                }

                ITypeSymbol t = target.TypeArguments[0];
                if (t is ITypeParameterSymbol || t.TypeKind == TypeKind.Error || !IsReachableType(t))
                {
                    // A private data type cannot be written into a typeof() here — and could not
                    // be consumed from another assembly either, so nothing is lost by omitting it.
                    continue;
                }

                switch (target.Name)
                {
                    case "GetData":
                        readSet.Add(t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                        break;
                    case "SetData":
                        writeSet.Add(t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                        break;
                }
            }
        }

        foreach (var attr in symbol.GetAttributes())
        {
            INamedTypeSymbol? ac = attr.AttributeClass;
            if (ac is null
                || !ac.IsGenericType
                || ac.ContainingNamespace?.ToDisplayString() != "Paradise.BT")
            {
                continue;
            }

            ITypeSymbol declared = ac.TypeArguments[0];
            if (!IsReachableType(declared))
            {
                continue;
            }

            string t = declared.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            switch (ac.Name)
            {
                case "ReadsAttribute":
                    readSet.Add(t);
                    break;
                case "WritesAttribute":
                    writeSet.Add(t);
                    break;
            }
        }

        reads = [.. readSet];
        writes = [.. writeSet];
    }

    private static bool HasGuidAttribute(INamedTypeSymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == GuidAttributeFullName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reachability for an accessed DATA type — same rule as <see cref="IsReachable"/>,
    /// over any type symbol.</summary>
    private static bool IsReachableType(ITypeSymbol symbol)
    {
        for (ITypeSymbol? current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Can an internal class in the same assembly name this type? Private and protected
    /// nested types cannot be, at any depth.</summary>
    private static bool IsReachable(INamedTypeSymbol symbol)
    {
        for (ITypeSymbol? current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One module initializer per assembly that declares node types, registering every one.
    ///
    /// This is what removes the hand-written RegisterAll()/Register&lt;T&gt;() calls: a forgotten
    /// registration used to surface as a refusal when a layout was built, a long way from the node
    /// somebody added. A module initializer runs before any of the assembly's types are used, so
    /// the table is populated by the time anything can ask.
    /// </summary>
    private static void EmitRegistration(SourceProductionContext spc, ImmutableArray<RegistrableNode> nodes)
    {
        if (nodes.IsDefaultOrEmpty)
        {
            return;
        }

        // A partial struct is seen once per declaration; the access union is symbol-based and so
        // identical on each, and one row per node is what the metadata reader expects.
        var distinct = nodes
            .GroupBy(static n => n.Name, StringComparer.Ordinal)
            .Select(static g => g.First())
            .OrderBy(static n => n.Name, StringComparer.Ordinal)
            .ToImmutableArray();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        // Each node's access, published as metadata so a CONSUMING assembly's binding can read it
        // where no body exists — the generated counterpart of hand-written [Reads<T>]/[Writes<T>].
        foreach (var node in distinct)
        {
            sb.Append($"[assembly: global::Paradise.BT.NodeAccess(typeof({node.Name})");
            if (!node.Reads.IsEmpty)
            {
                sb.Append($", Reads = new[] {{ {string.Join(", ", node.Reads.Select(static t => $"typeof({t})"))} }}");
            }

            if (!node.Writes.IsEmpty)
            {
                sb.Append($", Writes = new[] {{ {string.Join(", ", node.Writes.Select(static t => $"typeof({t})"))} }}");
            }

            sb.AppendLine(")]");
        }

        sb.AppendLine();
        sb.AppendLine("namespace Paradise.BT.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Registers this assembly's node types with NodeTypeRegistry.</summary>");
        sb.AppendLine("    internal static class NodeTypeRegistration");
        sb.AppendLine("    {");
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        internal static void Register()");
        sb.AppendLine("        {");

        foreach (var node in distinct)
        {
            sb.AppendLine(
                $"            global::Paradise.BT.NodeTypeRegistry.Register<{node.Name}>();");
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource("NodeTypeRegistration.g.cs", sb.ToString());
    }

    private readonly struct RegistrableNode : System.IEquatable<RegistrableNode>
    {
        public readonly string Name;
        public readonly ImmutableArray<string> Reads;
        public readonly ImmutableArray<string> Writes;

        public RegistrableNode(string name, ImmutableArray<string> reads, ImmutableArray<string> writes)
        {
            Name = name;
            Reads = reads;
            Writes = writes;
        }

        public bool Equals(RegistrableNode other) =>
            Name == other.Name
            && Reads.SequenceEqual(other.Reads)
            && Writes.SequenceEqual(other.Writes);

        public override bool Equals(object? obj) => obj is RegistrableNode other && Equals(other);

        public override int GetHashCode() => Name.GetHashCode();
    }

    private static string StripNodeSuffix(string name)
    {
        return name.EndsWith("Node", StringComparison.Ordinal)
            ? name.Substring(0, name.Length - 4)
            : name;
    }

    private static string GenerateWrapper(NodeInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (info.Namespace is not null)
        {
            sb.AppendLine($"namespace {info.Namespace}.Builder;");
        }
        else
        {
            sb.AppendLine("namespace Paradise.BT.Builder;");
        }
        sb.AppendLine();

        string baseClass = info.Cardinality switch
        {
            0 => $"global::Paradise.BT.Builder.LeafNode<{info.FullyQualifiedStructName}>",
            1 => $"global::Paradise.BT.Builder.DecoratorNode<{info.FullyQualifiedStructName}>",
            2 => $"global::Paradise.BT.Builder.CompositeNode<{info.FullyQualifiedStructName}>",
            _ => throw new System.InvalidOperationException()
        };

        sb.AppendLine($"public sealed class {info.GeneratedClassName} : {baseClass}");
        sb.AppendLine("{");

        // Generate constructor
        switch (info.Cardinality)
        {
            case 0: // Leaf
                GenerateLeafConstructor(sb, info);
                break;
            case 1: // Decorator
                GenerateDecoratorConstructor(sb, info);
                break;
            case 2: // Composite
                GenerateCompositeConstructor(sb, info);
                break;
        }

        sb.AppendLine("}");

        // A static entry point beside the class, so a tree reads Seq(Delay(0.5f)) rather than
        // new Sequence(new Delay(0.5f)). Contributed to one partial class per assembly, which a
        // tree brings into scope with `using static`.
        //
        // This is a factory method, which is the shape that was just deleted from this library —
        // and the difference is the RETURN TYPE. BuiltInBehaviorNodes.Sequence returned a
        // BehaviorNodeDefinition, discarding every trace of what it built, so a binding could not
        // see through it. This returns the BUILDER, which carries its node as a generic argument
        // on its base, so the node type survives the call.
        sb.AppendLine();
        sb.AppendLine("public static partial class Nodes");
        sb.AppendLine("{");
        if (!info.Fields.IsEmpty)
        {
            sb.AppendLine(RequireNamedArguments);
        }

        sb.Append($"    public static {info.GeneratedClassName} {info.GeneratedClassName}(");
        sb.Append(info.Cardinality switch
        {
            0 => BuildParamList(info.Fields, includeChild: false),
            1 => BuildParamList(info.Fields, includeChild: true),
            _ => BuildCompositeParamList(info.Fields),
        });

        // Value arguments are forwarded by name — the constructor is [RequireNamedArguments],
        // and this forwarder is its first caller.
        sb.Append(") => new(");
        sb.Append(info.Cardinality switch
        {
            0 => string.Join(", ", info.Fields.Select(f => NamedArgument(f.Name))),
            1 => string.Join(
                ", ",
                info.Fields.Where(f => f.DefaultLiteral is null).Select(f => NamedArgument(f.Name))
                    .Concat(["child"])
                    .Concat(info.Fields.Where(f => f.DefaultLiteral is not null).Select(f => NamedArgument(f.Name)))),
            _ => string.Join(
                ", ",
                info.Fields.Select(f => NamedArgument(f.Name)).Concat(["children"])),
        });

        sb.AppendLine(");");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private const string RequireNamedArguments = "    [global::Paradise.BT.RequireNamedArguments]";

    private static void GenerateLeafConstructor(StringBuilder sb, NodeInfo info)
    {
        if (info.Fields.IsEmpty)
        {
            sb.AppendLine($"    public {info.GeneratedClassName}() : base({Construction(info)}) {{ }}");
        }
        else
        {
            var paramList = BuildParamList(info.Fields, includeChild: false);
            sb.AppendLine(RequireNamedArguments);
            sb.AppendLine($"    public {info.GeneratedClassName}({paramList}) : base({Construction(info)}) {{ }}");
        }
    }

    private static void GenerateDecoratorConstructor(StringBuilder sb, NodeInfo info)
    {
        var paramList = BuildParamList(info.Fields, includeChild: true);
        sb.AppendLine(RequireNamedArguments);
        sb.AppendLine($"    public {info.GeneratedClassName}({paramList}) : base({Construction(info)}, child) {{ }}");
    }

    private static void GenerateCompositeConstructor(StringBuilder sb, NodeInfo info)
    {
        var paramList = BuildCompositeParamList(info.Fields);
        if (!info.Fields.IsEmpty)
        {
            sb.AppendLine(RequireNamedArguments);
        }

        sb.AppendLine($"    public {info.GeneratedClassName}({paramList}) : base({Construction(info)}, children) {{ }}");
    }

    /// <summary>Through the node's own constructor when it declares one (it may initialize
    /// non-exposed state), otherwise by object initializer over public fields.</summary>
    private static string Construction(NodeInfo info)
    {
        if (info.UseConstructor)
        {
            return $"new {info.FullyQualifiedStructName}("
                + string.Join(", ", info.Fields.Select(f => ToCamelCase(f.Name)))
                + ")";
        }

        if (info.Fields.IsEmpty)
        {
            return $"new {info.FullyQualifiedStructName}()";
        }

        var assignments = new System.Collections.Generic.List<string>();
        foreach (var field in info.Fields)
        {
            assignments.Add($"{field.Name} = {ToCamelCase(field.Name)}");
        }

        return $"new {info.FullyQualifiedStructName} {{ {string.Join(", ", assignments)} }}";
    }

    // An optional parameter may precede `params children`, so declared defaults survive on
    // composites too.
    private static string BuildCompositeParamList(ImmutableArray<FieldInfo> fields)
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var field in fields)
        {
            parts.Add(Parameter(field));
        }

        parts.Add("params global::System.ReadOnlySpan<global::Paradise.BT.Builder.BTreeNode> children");
        return string.Join(", ", parts);
    }

    /// <summary>Required parameters, then the decorator's child, then optional ones. Constructor
    /// parameters are already required-then-optional (C# insists), so their declared order is
    /// preserved.</summary>
    private static string BuildParamList(ImmutableArray<FieldInfo> fields, bool includeChild)
    {
        var parts = new System.Collections.Generic.List<string>();

        foreach (var field in fields)
        {
            if (field.DefaultLiteral is null)
            {
                parts.Add(Parameter(field));
            }
        }

        if (includeChild)
        {
            parts.Add("global::Paradise.BT.Builder.BTreeNode child");
        }

        foreach (var field in fields)
        {
            if (field.DefaultLiteral is not null)
            {
                parts.Add(Parameter(field));
            }
        }

        return string.Join(", ", parts);
    }

    private static string NamedArgument(string fieldName)
    {
        string name = ToCamelCase(fieldName);
        return $"{name}: {name}";
    }

    private static string Parameter(FieldInfo field) =>
        field.DefaultLiteral is null
            ? $"{field.TypeName} {ToCamelCase(field.Name)}"
            : $"{field.TypeName} {ToCamelCase(field.Name)} = {field.DefaultLiteral}";

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private readonly struct NodeInfo : System.IEquatable<NodeInfo>
    {
        public readonly string StructName;
        public readonly string FullyQualifiedStructName;
        public readonly string GeneratedClassName;
        public readonly string? Namespace;
        public readonly int Cardinality;
        public readonly bool HasGuid;
        public readonly bool IsUnmanaged;
        public readonly bool UseConstructor;
        public readonly bool HasMultipleConstructors;
        public readonly ImmutableArray<string> UnexposedPublicFields;
        public readonly ImmutableArray<FieldInfo> Fields;
        public readonly Location? Location;

        public NodeInfo(
            string structName,
            string fullyQualifiedStructName,
            string generatedClassName,
            string? ns,
            int cardinality,
            bool hasGuid,
            bool isUnmanaged,
            bool useConstructor,
            bool hasMultipleConstructors,
            ImmutableArray<string> unexposedPublicFields,
            ImmutableArray<FieldInfo> fields,
            Location? location)
        {
            StructName = structName;
            FullyQualifiedStructName = fullyQualifiedStructName;
            GeneratedClassName = generatedClassName;
            Namespace = ns;
            Cardinality = cardinality;
            HasGuid = hasGuid;
            IsUnmanaged = isUnmanaged;
            UseConstructor = useConstructor;
            HasMultipleConstructors = hasMultipleConstructors;
            UnexposedPublicFields = unexposedPublicFields;
            Fields = fields;
            Location = location;
        }

        public bool Equals(NodeInfo other) =>
            StructName == other.StructName
            && FullyQualifiedStructName == other.FullyQualifiedStructName
            && GeneratedClassName == other.GeneratedClassName
            && Namespace == other.Namespace
            && Cardinality == other.Cardinality
            && HasGuid == other.HasGuid
            && IsUnmanaged == other.IsUnmanaged
            && UseConstructor == other.UseConstructor
            && HasMultipleConstructors == other.HasMultipleConstructors
            && UnexposedPublicFields.SequenceEqual(other.UnexposedPublicFields)
            && Fields.SequenceEqual(other.Fields);

        public override bool Equals(object? obj) => obj is NodeInfo other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = FullyQualifiedStructName.GetHashCode();
                hash = hash * 31 + Cardinality;
                hash = hash * 31 + (HasGuid ? 1 : 0);
                hash = hash * 31 + (IsUnmanaged ? 1 : 0);
                hash = hash * 31 + (UseConstructor ? 1 : 0);
                return hash;
            }
        }
    }

    private readonly struct FieldInfo : System.IEquatable<FieldInfo>
    {
        public readonly string Name;
        public readonly string TypeName;

        /// <summary>Rendered default value for an optional parameter, or null for a required
        /// one. Constructor parameters keep their declared default; the fallback field surface
        /// marks the first field required and the rest <c>default</c>.</summary>
        public readonly string? DefaultLiteral;

        public FieldInfo(string name, string typeName, string? defaultLiteral)
        {
            Name = name;
            TypeName = typeName;
            DefaultLiteral = defaultLiteral;
        }

        public bool Equals(FieldInfo other) =>
            Name == other.Name && TypeName == other.TypeName && DefaultLiteral == other.DefaultLiteral;

        public override bool Equals(object? obj) => obj is FieldInfo other && Equals(other);

        public override int GetHashCode() => Name.GetHashCode() ^ TypeName.GetHashCode();
    }
}
