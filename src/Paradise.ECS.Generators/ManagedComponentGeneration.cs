using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Paradise.ECS.Generators;

/// <summary>Emits managed component declarations and their per-assembly runtime metadata.</summary>
internal static class ManagedComponentGeneration
{
    public static ManagedComponentInfo Extract(INamedTypeSymbol symbol, AttributeData attribute)
    {
        var manualId = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Id").Value.Value is int id && id >= 0 ? (int?)id : null;
        var snapshot = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Snapshot").Value.Value is int policy ? policy : 0;
        var guidArgument = attribute.ConstructorArguments.FirstOrDefault().Value as string;
        var guid = guidArgument ?? GeneratorUtilities.ExtractGuid(symbol);
        var invalidGuid = guid != null && !Guid.TryParseExact(guid, "D", out _) ? guid : null;
        var isPartial = symbol.DeclaringSyntaxReferences.All(r =>
            r.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword));
        var isAccessible = IsAccessible(symbol);
        string? invalidContainingType = symbol.IsGenericType ? symbol.ToDisplayString() : null;
        for (var parent = symbol.ContainingType; parent != null; parent = parent.ContainingType)
        {
            if (parent.IsGenericType)
                invalidContainingType = parent.ToDisplayString();
            isPartial &= parent.DeclaringSyntaxReferences.All(r =>
                r.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword));
            isAccessible &= IsAccessible(parent);
        }
        var cloneImplemented = symbol.AllInterfaces.Any(i =>
            i.OriginalDefinition.ToDisplayString() == "Paradise.ECS.IManagedClone<TSelf>" &&
            i.TypeArguments.Length == 1 && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], symbol));
        var name = GeneratorUtilities.GetFullyQualifiedName(symbol);
        var slotName = "Managed_" + symbol.Name;
        var className = GeneratorUtilities.EscapeIdentifier(symbol.Name);
        var slotFullName = name.Substring(0, name.Length - className.Length) + slotName;
        var slot = new TypeInfo(TypeKind.Managed, slotFullName,
            symbol.Locations.FirstOrDefault() ?? Location.None, false,
            GeneratorUtilities.GetNamespace(symbol), slotName, GeneratorUtilities.GetContainingTypes(symbol),
            invalidGuid == null ? guid : null, invalidContainingType, true, manualId);
        return new ManagedComponentInfo(slot, name, className, GeneratorUtilities.GetTypeKeyword(symbol),
            symbol.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class && isPartial && !symbol.IsStatic,
            symbol.IsSealed, snapshot, cloneImplemented, invalidGuid, isAccessible);
    }

    private static bool IsAccessible(INamedTypeSymbol symbol)
        => symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;

    public static ImmutableArray<ManagedComponentInfo> Validate(
        SourceProductionContext context, ImmutableArray<ManagedComponentInfo> components)
    {
        var valid = ImmutableArray.CreateBuilder<ManagedComponentInfo>();
        foreach (var info in components)
        {
            if (!info.IsPartialClass)
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.ManagedComponentMustBePartialClass,
                    info.Slot.Location, info.FullyQualifiedName));
            if (!info.IsSealed)
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.ManagedComponentMustBeSealed,
                    info.Slot.Location, info.FullyQualifiedName));
            if (info.Snapshot == 1 && !info.HasClone)
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.ManagedComponentCloneRequired,
                    info.Slot.Location, info.FullyQualifiedName));
            if (info.InvalidGuid != null)
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.ManagedComponentInvalidGuid,
                    info.Slot.Location, info.FullyQualifiedName, info.InvalidGuid));
            if (info.Snapshot is < 0 or > 2)
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.ManagedComponentInvalidSnapshot,
                    info.Slot.Location, info.FullyQualifiedName, info.Snapshot));
            if (!info.IsAccessible)
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.ManagedComponentMustBeAccessible,
                    info.Slot.Location, info.FullyQualifiedName));
            if (info.Slot.InvalidContainingType != null)
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.UnsupportedContainingType,
                    info.Slot.Location, info.FullyQualifiedName, info.Slot.InvalidContainingType, "a generic type"));
            if (info.IsValid)
                valid.Add(info);
        }
        return valid.ToImmutable();
    }

    public static void Generate(
        SourceProductionContext context, ImmutableArray<ManagedComponentInfo> components,
        string rootNamespace, string maskType)
    {
        foreach (var info in components)
            GenerateClass(context, info);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"namespace {rootNamespace};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Managed component metadata for this assembly.</summary>");
        sb.AppendLine("public static class ManagedRegistry");
        sb.AppendLine("{");
        // Initialize lazily, after the component registry's module initializer assigns slot IDs.
        sb.AppendLine("    public static global::System.Collections.Immutable.ImmutableArray<global::Paradise.ECS.ManagedTypeInfo> TypeInfos => Cache.TypeInfos;");
        sb.AppendLine($"    public static {maskType} ManagedSlotMask => Cache.ManagedSlotMask;");
        sb.AppendLine();
        sb.AppendLine("    private static class Cache");
        sb.AppendLine("    {");
        sb.AppendLine("        internal static readonly global::System.Collections.Immutable.ImmutableArray<global::Paradise.ECS.ManagedTypeInfo> TypeInfos =");
        sb.AppendLine("            global::System.Collections.Immutable.ImmutableArray.Create<global::Paradise.ECS.ManagedTypeInfo>(");
        for (var i = 0; i < components.Length; i++)
        {
            var info = components[i];
            var expression = info.Snapshot == 1
                ? $"global::Paradise.ECS.ManagedTypeInfo.CreateClone<global::{info.FullyQualifiedName}>()"
                : $"global::Paradise.ECS.ManagedTypeInfo.Create<global::{info.FullyQualifiedName}>(global::Paradise.ECS.ManagedSnapshot.{(info.Snapshot == 2 ? "Skip" : "Reference")})";
            sb.AppendLine($"                {expression}{(i + 1 == components.Length ? ");" : ",")}");
        }
        sb.AppendLine();
        sb.AppendLine($"        internal static readonly {maskType} ManagedSlotMask = CreateMask();");
        sb.AppendLine();
        sb.AppendLine($"        private static {maskType} CreateMask()");
        sb.AppendLine("        {");
        sb.AppendLine($"            var mask = default({maskType});");
        foreach (var info in components)
            sb.AppendLine($"            mask = mask.Set(global::{info.FullyQualifiedName}.SlotTypeId.Value);");
        sb.AppendLine("            return mask;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        context.AddSource("ManagedRegistry.g.cs", sb.ToString());
    }

    private static void GenerateClass(SourceProductionContext context, ManagedComponentInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (info.Slot.Namespace != null)
        {
            sb.AppendLine($"namespace {info.Slot.Namespace};");
            sb.AppendLine();
        }
        var indent = "";
        foreach (var containing in info.Slot.ContainingTypes)
        {
            sb.AppendLine($"{indent}partial {containing.Keyword} {containing.Name}");
            sb.AppendLine($"{indent}{{");
            indent += "    ";
        }
        sb.AppendLine($"{indent}partial {info.Keyword} {info.TypeName} : global::Paradise.ECS.IManagedComponent, global::Paradise.ECS.IEntityComponent<global::{info.FullyQualifiedName}>");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    /// <summary>The component ID of this managed type's generated handle slot.</summary>");
        sb.AppendLine($"{indent}    public static global::Paradise.ECS.ComponentId SlotTypeId => global::{info.Slot.FullyQualifiedName}.TypeId;");
        sb.AppendLine($"{indent}    /// <summary>The stable GUID for this managed component.</summary>");
        sb.AppendLine($"{indent}    public static global::System.Guid Guid => global::{info.Slot.FullyQualifiedName}.Guid;");
        ComponentGenerator.GenerateEntityComponentOperations(sb, indent, info.FullyQualifiedName, TypeKind.Managed);
        sb.AppendLine($"{indent}}}");
        for (var i = info.Slot.ContainingTypes.Length - 1; i >= 0; i--)
            sb.AppendLine($"{new string(' ', i * 4)}}}");
        context.AddSource($"{info.FullyQualifiedName.Replace(".", "_").Replace("+", "_")}.ManagedComponent.g.cs", sb.ToString());
    }
}

internal readonly struct ManagedComponentInfo
{
    public TypeInfo Slot { get; }
    public string FullyQualifiedName { get; }
    public string TypeName { get; }
    public string Keyword { get; }
    public bool IsPartialClass { get; }
    public bool IsSealed { get; }
    public int Snapshot { get; }
    public bool HasClone { get; }
    public string? InvalidGuid { get; }
    public bool IsAccessible { get; }
    public bool IsValid => IsPartialClass && IsSealed && Snapshot is >= 0 and <= 2 && (Snapshot != 1 || HasClone) &&
        InvalidGuid == null && Slot.InvalidContainingType == null && IsAccessible;

    public ManagedComponentInfo(TypeInfo slot, string fullyQualifiedName, string typeName, string keyword,
        bool isPartialClass, bool isSealed, int snapshot, bool hasClone, string? invalidGuid, bool isAccessible)
    {
        Slot = slot;
        FullyQualifiedName = fullyQualifiedName;
        TypeName = typeName;
        Keyword = keyword;
        IsPartialClass = isPartialClass;
        IsSealed = isSealed;
        Snapshot = snapshot;
        HasClone = hasClone;
        InvalidGuid = invalidGuid;
        IsAccessible = isAccessible;
    }
}
