using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
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

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => node is StructDeclarationSyntax s && s.AttributeLists.Count > 0,
            transform: static (ctx, ct) => GetNodeInfo(ctx, ct)
        ).Where(static info => info.HasValue)
         .Select(static (info, _) => info!.Value);

        // Registration is emitted from a SEPARATE pass over every INodeData struct, not from the
        // [Builder] pass above. The two sets are not the same: DelayTimerNode is registerable and
        // used to have no [Builder] at all, back when a factory built it. Keying registration
        // on [Builder] would silently drop it, and with it every timer node in every tree.
        var registrable = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => node is StructDeclarationSyntax { BaseList.Types.Count: > 0 },
            transform: static (ctx, ct) => GetRegistrableNode(ctx, ct)
        ).Where(static name => name is not null)
         .Select(static (name, _) => name!);

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

            var source = GenerateWrapper(info);
            spc.AddSource($"{info.GeneratedClassName}.g.cs", source);
        });
    }

    private static NodeInfo? GetNodeInfo(GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var structDecl = (StructDeclarationSyntax)ctx.Node;
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

        // Get public fields for constructor parameters
        var fields = ImmutableArray<FieldInfo>.Empty;
        if (cardinality != 2) // Not composite — composites have no struct fields in constructor
        {
            var builder = ImmutableArray.CreateBuilder<FieldInfo>();
            foreach (var member in structSymbol.GetMembers())
            {
                if (member is IFieldSymbol field
                    && field.DeclaredAccessibility == Accessibility.Public
                    && !field.IsStatic
                    && !field.IsConst
                    && field.Type.IsValueType)
                {
                    builder.Add(new FieldInfo(
                        field.Name,
                        field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    ));
                }
            }
            fields = builder.ToImmutable();
        }

        string fullyQualifiedName = structSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return new NodeInfo(
            structName,
            fullyQualifiedName,
            generatedName,
            ns,
            cardinality,
            hasGuid,
            isUnmanaged,
            fields,
            structDecl.GetLocation()
        );
    }

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
    private static string? GetRegistrableNode(
        GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ctx.SemanticModel.GetDeclaredSymbol((StructDeclarationSyntax)ctx.Node, ct)
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

        return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
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
    private static void EmitRegistration(SourceProductionContext spc, ImmutableArray<string> names)
    {
        if (names.IsDefaultOrEmpty)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Paradise.BT.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Registers this assembly's node types with NodeTypeRegistry.</summary>");
        sb.AppendLine("internal static class NodeTypeRegistration");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");

        foreach (var name in names.Distinct().OrderBy(static n => n, StringComparer.Ordinal))
        {
            sb.AppendLine(
                $"        global::Paradise.BT.NodeTypeRegistry.Register<{name}>();");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource("NodeTypeRegistration.g.cs", sb.ToString());
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
        sb.Append($"    public static {info.GeneratedClassName} {info.GeneratedClassName}(");
        sb.Append(info.Cardinality switch
        {
            0 => BuildParamList(info.Fields, includeChild: false),
            1 => BuildParamList(info.Fields, includeChild: true),
            _ => "params global::System.ReadOnlySpan<global::Paradise.BT.Builder.BTreeNode> children",
        });

        sb.Append(") => new(");
        sb.Append(info.Cardinality switch
        {
            0 => string.Join(", ", info.Fields.Select(f => ToCamelCase(f.Name))),
            1 => string.Join(
                ", ",
                info.Fields.Take(1).Select(f => ToCamelCase(f.Name))
                    .Concat(["child"])
                    .Concat(info.Fields.Skip(1).Select(f => ToCamelCase(f.Name)))),
            _ => "children",
        });

        sb.AppendLine(");");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void GenerateLeafConstructor(StringBuilder sb, NodeInfo info)
    {
        if (info.Fields.IsEmpty)
        {
            sb.AppendLine($"    public {info.GeneratedClassName}() : base(new {info.FullyQualifiedStructName}()) {{ }}");
        }
        else
        {
            var paramList = BuildParamList(info.Fields, includeChild: false);
            var initializer = BuildStructInitializer(info.FullyQualifiedStructName, info.Fields);
            sb.AppendLine($"    public {info.GeneratedClassName}({paramList}) : base({initializer}) {{ }}");
        }
    }

    private static void GenerateDecoratorConstructor(StringBuilder sb, NodeInfo info)
    {
        var paramList = BuildParamList(info.Fields, includeChild: true);
        var initializer = BuildStructInitializer(info.FullyQualifiedStructName, info.Fields);
        sb.AppendLine($"    public {info.GeneratedClassName}({paramList}) : base({initializer}, child) {{ }}");
    }

    private static void GenerateCompositeConstructor(StringBuilder sb, NodeInfo info)
    {
        sb.AppendLine($"    public {info.GeneratedClassName}(params global::System.ReadOnlySpan<global::Paradise.BT.Builder.BTreeNode> children) : base(new {info.FullyQualifiedStructName}(), children) {{ }}");
    }

    private static string BuildParamList(ImmutableArray<FieldInfo> fields, bool includeChild)
    {
        var parts = new System.Collections.Generic.List<string>();

        // Required fields first (no default), then child for decorators, then optional fields
        var requiredFields = new System.Collections.Generic.List<FieldInfo>();
        var optionalFields = new System.Collections.Generic.List<FieldInfo>();

        foreach (var field in fields)
        {
            // First field is always required, rest get defaults
            if (requiredFields.Count == 0)
                requiredFields.Add(field);
            else
                optionalFields.Add(field);
        }

        foreach (var field in requiredFields)
        {
            parts.Add($"{field.TypeName} {ToCamelCase(field.Name)}");
        }

        if (includeChild)
        {
            parts.Add("global::Paradise.BT.Builder.BTreeNode child");
        }

        foreach (var field in optionalFields)
        {
            parts.Add($"{field.TypeName} {ToCamelCase(field.Name)} = default");
        }

        return string.Join(", ", parts);
    }

    private static string BuildStructInitializer(string structName, ImmutableArray<FieldInfo> fields)
    {
        if (fields.IsEmpty)
            return $"new {structName}()";

        var assignments = new System.Collections.Generic.List<string>();
        foreach (var field in fields)
        {
            assignments.Add($"{field.Name} = {ToCamelCase(field.Name)}");
        }

        return $"new {structName} {{ {string.Join(", ", assignments)} }}";
    }

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
                return hash;
            }
        }
    }

    private readonly struct FieldInfo : System.IEquatable<FieldInfo>
    {
        public readonly string Name;
        public readonly string TypeName;

        public FieldInfo(string name, string typeName)
        {
            Name = name;
            TypeName = typeName;
        }

        public bool Equals(FieldInfo other) => Name == other.Name && TypeName == other.TypeName;

        public override bool Equals(object? obj) => obj is FieldInfo other && Equals(other);

        public override int GetHashCode() => Name.GetHashCode() ^ TypeName.GetHashCode();
    }
}
