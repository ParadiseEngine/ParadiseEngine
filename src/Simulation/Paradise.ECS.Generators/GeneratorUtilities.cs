using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Paradise.ECS.Generators;

/// <summary>Shared utilities for source generators.</summary>
internal static class GeneratorUtilities
{
    private const string GuidAttributeName = "GuidAttribute";
    private const string GuidAttributeFullName = "System.Runtime.InteropServices." + GuidAttributeName;

    // All generators must agree on the namespace of generated component and tag types.
    public static string GetRootNamespace(Compilation compilation, AnalyzerConfigOptionsProvider options)
    {
        var attribute = compilation.Assembly.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "Paradise.ECS.ComponentRegistryNamespaceAttribute");
        if (attribute?.ConstructorArguments.FirstOrDefault().Value is string value)
            return value;
        options.GlobalOptions.TryGetValue("build_property.RootNamespace", out var rootNamespace);
        return rootNamespace ?? "Paradise.ECS";
    }

    /// <summary>The optimal mask type string based on the number of bits required.</summary>
    /// <param name="requiredBits">The number of bits required.</param>
    /// <returns>The fully qualified mask type string.</returns>
    public static string GetOptimalMaskType(int requiredBits)
    {
        if (requiredBits <= 32) return "global::Paradise.ECS.SmallBitSet<uint>";
        if (requiredBits <= 64) return "global::Paradise.ECS.SmallBitSet<ulong>";
        if (requiredBits <= 128) return "global::Paradise.ECS.ImmutableBitSet<global::Paradise.ECS.Bit128>";
        if (requiredBits <= 256) return "global::Paradise.ECS.ImmutableBitSet<global::Paradise.ECS.Bit256>";
        if (requiredBits <= 512) return "global::Paradise.ECS.ImmutableBitSet<global::Paradise.ECS.Bit512>";
        if (requiredBits <= 1024) return "global::Paradise.ECS.ImmutableBitSet<global::Paradise.ECS.Bit1024>";

        var capacity = ((requiredBits + 255) / 256) * 256;
        return $"global::Paradise.ECS.ImmutableBitSet<global::Paradise.ECS.Bit{capacity}>";
    }

    /// <summary>The fully qualified name of a type symbol without the "global::" prefix.</summary>
    public static string GetFullyQualifiedName(INamedTypeSymbol symbol)
    {
        var fqn = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fqn.StartsWith("global::", StringComparison.Ordinal) ? fqn.Substring(8) : fqn;
    }

    /// <summary>The namespace of a type symbol, or null if it's in the global namespace.</summary>
    public static string? GetNamespace(INamedTypeSymbol symbol)
        => symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString();

    /// <summary>The type keyword for a type symbol (class, struct, record class, record struct, interface).</summary>
    public static string GetTypeKeyword(INamedTypeSymbol type) => type.TypeKind switch
    {
        Microsoft.CodeAnalysis.TypeKind.Class => type.IsRecord ? "record class" : "class",
        Microsoft.CodeAnalysis.TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
        Microsoft.CodeAnalysis.TypeKind.Interface => "interface",
        _ => "struct"
    };

    /// <summary>The containing types for a nested type, ordered from outermost to innermost.</summary>
    public static ImmutableArray<ContainingTypeInfo> GetContainingTypes(INamedTypeSymbol symbol)
    {
        var list = new List<ContainingTypeInfo>();
        for (var parent = symbol.ContainingType; parent != null; parent = parent.ContainingType)
            list.Add(new ContainingTypeInfo(EscapeIdentifier(parent.Name), GetTypeKeyword(parent)));
        list.Reverse();
        return list.ToImmutableArray();
    }

    public static string EscapeIdentifier(string name)
        => SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;

    /// <summary>Extracts type information from a generator attribute syntax context.</summary>
    public static TypeInfo? ExtractTypeInfo(GeneratorAttributeSyntaxContext context, TypeKind kind)
    {
        if (context.TargetSymbol is not INamedTypeSymbol typeSymbol || typeSymbol.TypeKind != Microsoft.CodeAnalysis.TypeKind.Struct)
            return null;

        var fullyQualifiedName = GetFullyQualifiedName(typeSymbol);
        var ns = GetNamespace(typeSymbol);
        var containingTypes = GetContainingTypes(typeSymbol);

        string? invalidContainingType = null;
        for (var parent = typeSymbol.ContainingType; parent != null; parent = parent.ContainingType)
        {
            if (parent.IsGenericType)
            {
                invalidContainingType = parent.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
                break;
            }
        }

        // Extract the manual ID from the [Component]/[Tag] attribute
        int? manualId = null;
        foreach (var attr in context.Attributes)
        {
            foreach (var arg in attr.NamedArguments)
            {
                if (arg.Key == "Id" && arg.Value.Value is int id && id >= 0)
                    manualId = id;
            }
        }

        // Stable identity comes from the standard [System.Runtime.InteropServices.Guid] attribute
        var guid = ExtractGuid(typeSymbol);

        var hasInstanceFields = typeSymbol.GetMembers().OfType<IFieldSymbol>().Any(f => !f.IsStatic);

        return new TypeInfo(
            kind,
            fullyQualifiedName,
            typeSymbol.Locations.FirstOrDefault() ?? Location.None,
            typeSymbol.IsUnmanagedType,
            ns,
            typeSymbol.Name,
            containingTypes,
            guid,
            invalidContainingType,
            hasInstanceFields,
            manualId);
    }

    /// <summary>Reads the stable GUID declared by <c>[System.Runtime.InteropServices.Guid]</c> on a type.</summary>
    /// <remarks>
    /// A malformed value needs no diagnostic of ours: the compiler already rejects it with CS0591
    /// before this generator's output could matter. Parsing is therefore restricted to the exact
    /// "D" format the compiler accepts, so a value this method calls valid is one that compiles.
    /// </remarks>
    /// <returns>The GUID string, or <c>null</c> when the type declares no usable GUID.</returns>
    internal static string? ExtractGuid(INamedTypeSymbol typeSymbol)
    {
        foreach (var attr in typeSymbol.GetAttributes())
        {
            var attributeClass = attr.AttributeClass;
            if (attributeClass is null || attributeClass.Name != GuidAttributeName)
                continue;
            if (attributeClass.ToDisplayString() != GuidAttributeFullName)
                continue;
            if (attr.ConstructorArguments.Length == 0 || attr.ConstructorArguments[0].Value is not string value)
                continue;

            return Guid.TryParseExact(value, "D", out _) ? value : null;
        }

        return null;
    }

    /// <summary>Processes and validates types, returning valid types and the optimal mask type.</summary>
    public static (List<TypeInfo> Valid, string MaskType) ProcessTypes(
        SourceProductionContext context,
        ImmutableArray<TypeInfo> types,
        int maxId,
        DiagnosticDescriptor notUnmanaged,
        DiagnosticDescriptor invalidContaining,
        DiagnosticDescriptor idExceedsLimit,
        DiagnosticDescriptor duplicateId,
        Func<TypeInfo, DiagnosticDescriptor?> additionalCheck)
    {
        if (types.IsEmpty)
            return (new List<TypeInfo>(), "global::Paradise.ECS.SmallBitSet<uint>");

        var sorted = types.OrderBy(t => t.FullyQualifiedName, StringComparer.Ordinal).ToList();
        var duplicateManualIds = new HashSet<int>();

        foreach (var t in sorted)
        {
            if (!t.IsUnmanaged && t.Kind != TypeKind.Managed)
                context.ReportDiagnostic(Diagnostic.Create(notUnmanaged, t.Location, t.FullyQualifiedName));
            if (t.InvalidContainingType != null)
                context.ReportDiagnostic(Diagnostic.Create(invalidContaining, t.Location, t.FullyQualifiedName, t.InvalidContainingType, "a generic type"));
            if (t.ManualId > maxId)
                context.ReportDiagnostic(Diagnostic.Create(idExceedsLimit, t.Location, t.FullyQualifiedName, t.ManualId, maxId));
            if (additionalCheck(t) is { } desc)
                context.ReportDiagnostic(Diagnostic.Create(desc, t.Location, t.FullyQualifiedName));
        }

        foreach (var group in sorted.Where(t => t.ManualId.HasValue).GroupBy(t => t.ManualId!.Value).Where(g => g.Count() > 1))
        {
            duplicateManualIds.Add(group.Key);
            context.ReportDiagnostic(Diagnostic.Create(duplicateId, group.First().Location, group.Key,
                string.Join(", ", group.Select(t => t.FullyQualifiedName))));
        }

        var valid = sorted.Where(t =>
            (t.IsUnmanaged || t.Kind == TypeKind.Managed) &&
            t.InvalidContainingType == null &&
            (!t.ManualId.HasValue || (t.ManualId.Value <= maxId && !duplicateManualIds.Contains(t.ManualId.Value))) &&
            (t.Kind != TypeKind.Tag || !t.HasInstanceFields)).ToList();

        var maxAssignedId = CalculateMaxAssignedId(valid);
        var requiredBits = maxAssignedId + 1;
        var maskType = GetOptimalMaskType(requiredBits);

        return (valid, maskType);
    }

    private readonly struct ComponentBitContribution(ISymbol symbol, string fullyQualifiedName, int? manualId)
        : IEquatable<ComponentBitContribution>
    {
        public ISymbol Symbol { get; } = symbol;
        public string FullyQualifiedName { get; } = fullyQualifiedName;
        public int? ManualId { get; } = manualId;

        public bool Equals(ComponentBitContribution other)
            => SymbolEqualityComparer.Default.Equals(Symbol, other.Symbol)
               && FullyQualifiedName == other.FullyQualifiedName && ManualId == other.ManualId;

        public override bool Equals(object? obj) => obj is ComponentBitContribution other && Equals(other);

        public override int GetHashCode()
            => (SymbolEqualityComparer.Default.GetHashCode(Symbol) * 397 ^ FullyQualifiedName.GetHashCode()) * 397
               ^ ManualId.GetHashCode();
    }

    /// <summary>Creates a per-attribute incremental provider for the registry capacity shared by component, query and system generation.</summary>
    /// <remarks>Per-tree inputs keep IDE incremental compilation cached; a compilation-wide scan would recompute every edit.</remarks>
    public static IncrementalValueProvider<int> CreateRequiredComponentBitsProvider(IncrementalGeneratorInitializationContext context)
    {
        var components = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "Paradise.ECS.ComponentAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) =>
                {
                    if (ctx.TargetSymbol is not INamedTypeSymbol symbol || !symbol.IsUnmanagedType)
                        return (ComponentBitContribution?)null;
                    var manualId = ctx.Attributes[0].NamedArguments.FirstOrDefault(a => a.Key == "Id").Value.Value is int id && id >= 0 ? (int?)id : null;
                    return new ComponentBitContribution(symbol, GetFullyQualifiedName(symbol), manualId);
                })
            .Where(static x => x.HasValue)
            .Select(static (x, _) => x!.Value)
            .Collect();

        var managed = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "Paradise.ECS.ManagedComponentAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) =>
                {
                    if (ctx.TargetSymbol is not INamedTypeSymbol symbol)
                        return (ComponentBitContribution?)null;
                    var info = ManagedComponentGeneration.Extract(symbol, ctx.Attributes[0]);
                    return info.IsValid ? new ComponentBitContribution(symbol, info.Slot.FullyQualifiedName, info.Slot.ManualId) : null;
                })
            .Where(static x => x.HasValue)
            .Select(static (x, _) => x!.Value)
            .Collect();

        var validTags = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "Paradise.ECS.TagAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) => ctx.TargetSymbol is INamedTypeSymbol symbol
                    && symbol.IsUnmanagedType && !symbol.IsGenericType
                    && !symbol.GetMembers().OfType<IFieldSymbol>().Any(f => !f.IsStatic))
            .Collect();

        var rootNamespace = context.CompilationProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, _) => GetRootNamespace(pair.Left, pair.Right));

        var references = context.CompilationProvider
            .Select(static (compilation, _) =>
                (HasManaged: compilation.ReferencedAssemblyNames.Any(a => a.Name == "Paradise.ECS.Managed"),
                 HasTags: compilation.ReferencedAssemblyNames.Any(a => a.Name == "Paradise.ECS.Tag")));

        return components.Combine(managed).Combine(validTags).Combine(rootNamespace).Combine(references)
            .Select(static (input, _) =>
            {
                var ((((componentInfos, managedInfos), tagResults), ns), refs) = input;
                var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                var manualIds = new HashSet<int>();
                var autoCount = 0;
                var hasUserEntityTags = false;
                var entityTagsFqn = ns + ".EntityTags";
                foreach (var info in componentInfos)
                {
                    if (!seen.Add(info.Symbol))
                        continue;
                    if (info.ManualId is int manual) manualIds.Add(manual); else autoCount++;
                    hasUserEntityTags |= info.FullyQualifiedName == entityTagsFqn;
                }
                if (refs.HasManaged)
                {
                    foreach (var info in managedInfos)
                    {
                        if (!seen.Add(info.Symbol))
                            continue;
                        if (info.ManualId is int manual) manualIds.Add(manual); else autoCount++;
                        hasUserEntityTags |= info.FullyQualifiedName == entityTagsFqn;
                    }
                }
                if (refs.HasTags && tagResults.Any(static valid => valid) && !hasUserEntityTags)
                    autoCount++;
                return CalculateMaxAssignedId(manualIds, autoCount) + 1;
            });
    }

    /// <summary>Calculates the maximum assigned ID for a list of types, considering manual IDs.</summary>
    public static int CalculateMaxAssignedId(List<TypeInfo> types)
    {
        var manualIds = new HashSet<int>(types.Where(t => t.ManualId.HasValue).Select(t => t.ManualId!.Value));
        return CalculateMaxAssignedId(manualIds, types.Count - manualIds.Count);
    }

    private static int CalculateMaxAssignedId(HashSet<int> manualIds, int autoCount)
    {
        var maxId = manualIds.Count > 0 ? manualIds.Max() : -1;

        for (int i = 0, nextId = 0; i < autoCount; i++, nextId++)
        {
            while (manualIds.Contains(nextId)) nextId++;
            if (nextId > maxId) maxId = nextId;
        }
        return maxId;
    }
}

/// <summary>Information about a containing type for nested types.</summary>
internal readonly struct ContainingTypeInfo
{
    public string Name { get; }
    public string Keyword { get; }

    public ContainingTypeInfo(string name, string keyword)
    {
        Name = name;
        Keyword = keyword;
    }
}

/// <summary>Represents the kind of type being processed.</summary>
internal enum TypeKind { Component, Tag, Managed }

/// <summary>Information about a type being processed by the generator.</summary>
internal readonly struct TypeInfo
{
    public TypeKind Kind { get; }
    public string FullyQualifiedName { get; }
    public Location Location { get; }
    public bool IsUnmanaged { get; }
    public string? Namespace { get; }
    public string TypeName { get; }
    public ImmutableArray<ContainingTypeInfo> ContainingTypes { get; }
    public string? Guid { get; }
    public string? InvalidContainingType { get; }
    public bool HasInstanceFields { get; }
    public int? ManualId { get; }

    public TypeInfo(
        TypeKind Kind,
        string FullyQualifiedName,
        Location Location,
        bool IsUnmanaged,
        string? Namespace,
        string TypeName,
        ImmutableArray<ContainingTypeInfo> ContainingTypes,
        string? Guid,
        string? InvalidContainingType,
        bool HasInstanceFields,
        int? ManualId)
    {
        this.Kind = Kind;
        this.FullyQualifiedName = FullyQualifiedName;
        this.Location = Location;
        this.IsUnmanaged = IsUnmanaged;
        this.Namespace = Namespace;
        this.TypeName = TypeName;
        this.ContainingTypes = ContainingTypes;
        this.Guid = Guid;
        this.InvalidContainingType = InvalidContainingType;
        this.HasInstanceFields = HasInstanceFields;
        this.ManualId = ManualId;
    }
}
