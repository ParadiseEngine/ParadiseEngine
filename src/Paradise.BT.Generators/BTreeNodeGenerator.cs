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
        sb.AppendLine($"    public {info.GeneratedClassName}(params global::Paradise.BT.Builder.BTreeNode[] children) : base(new {info.FullyQualifiedStructName}(), children) {{ }}");
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
