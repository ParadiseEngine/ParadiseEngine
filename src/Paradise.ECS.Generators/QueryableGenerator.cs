using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Paradise.ECS.Generators;

/// <summary>
/// Source generator that processes types marked with [Queryable] attribute.
/// Generates QueryableRegistry with static arrays for query descriptions and component masks.
/// </summary>
[Generator]
public class QueryableGenerator : IIncrementalGenerator
{
    private const string QueryableAttributeFullName = "Paradise.ECS.QueryableAttribute";
    private const string ComponentAttributeFullName = "Paradise.ECS.ComponentAttribute";
    private const string DefaultConfigAttributeFullName = "Paradise.ECS.DefaultConfigAttribute";
    private const string IConfigFullName = "Paradise.ECS.IConfig";
    private const string SuppressGlobalUsingsAttributeFullName = "Paradise.ECS.SuppressGlobalUsingsAttribute";
    private const string RegistryNamespaceAttributeFullName = "Paradise.ECS.ComponentRegistryNamespaceAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all ref structs with [Queryable] attribute
        var queryableTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                QueryableAttributeFullName,
                predicate: static (node, _) => node is StructDeclarationSyntax,
                transform: static (ctx, _) => GetQueryableInfo(ctx))
            .Where(static x => x is not null)
            .Select(static (x, _) => x!.Value);

        // Count components to determine bit type
        var componentCount = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ComponentAttributeFullName,
                predicate: static (node, _) => node is StructDeclarationSyntax,
                transform: static (ctx, _) => 1)
            .Collect()
            .Select(static (components, _) => components.Length);

        // Check for [assembly: SuppressGlobalUsings] attribute
        var suppressGlobalUsings = context.CompilationProvider
            .Select(static (compilation, _) =>
            {
                return compilation.Assembly.GetAttributes()
                    .Any(a => a.AttributeClass?.ToDisplayString() == SuppressGlobalUsingsAttributeFullName);
            });

        // Find DefaultConfig type for Data/ChunkData aliases
        var defaultConfig = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                DefaultConfigAttributeFullName,
                predicate: static (node, _) => node is StructDeclarationSyntax or ClassDeclarationSyntax,
                transform: static (ctx, _) =>
                {
                    if (ctx.TargetSymbol is not INamedTypeSymbol ts) return (string?)null;
                    var iConfig = ctx.SemanticModel.Compilation.GetTypeByMetadataName(IConfigFullName);
                    if (iConfig == null || !ts.AllInterfaces.Contains(iConfig, SymbolEqualityComparer.Default)) return null;
                    return GeneratorUtilities.GetFullyQualifiedName(ts);
                })
            .Where(static x => x is not null)
            .Collect()
            .Select(static (configs, _) => configs.FirstOrDefault());

        // Where TagGenerator puts EntityTags. Resolved the SAME way it resolves it — attribute,
        // then build property, then the default — because a queryable declaring [WithTag<T>] has to
        // name that type and generators cannot see each other's output.
        var rootNamespace = context.CompilationProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, _) =>
            {
                var nsAttr = pair.Left.Assembly.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == RegistryNamespaceAttributeFullName);
                if (nsAttr?.ConstructorArguments.FirstOrDefault().Value is string fromAttribute)
                    return fromAttribute;
                pair.Right.GlobalOptions.TryGetValue("build_property.RootNamespace", out var fromBuild);
                return fromBuild ?? "Paradise.ECS";
            });

        // Collect all queryables with component count, suppress flag, config, and root namespace
        var collected = queryableTypes.Collect()
            .Combine(componentCount)
            .Combine(suppressGlobalUsings)
            .Combine(defaultConfig)
            .Combine(rootNamespace);

        context.RegisterSourceOutput(collected, static (ctx, data) =>
            GenerateQueryableCode(
                ctx, data.Left.Left.Left.Left, data.Left.Left.Left.Right, data.Left.Left.Right,
                data.Left.Right, data.Right));
    }

    private static QueryableInfo? GetQueryableInfo(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol typeSymbol)
            return null;

        // Verify it's a struct
        if (typeSymbol.TypeKind != Microsoft.CodeAnalysis.TypeKind.Struct)
            return null;

        var fullyQualifiedName = GeneratorUtilities.GetFullyQualifiedName(typeSymbol);
        var isRefStruct = typeSymbol.IsRefLikeType;

        // Check if it's partial
        var isPartial = typeSymbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<StructDeclarationSyntax>()
            .Any(s => s.Modifiers.Any(m => m.Text == "partial"));

        var ns = GeneratorUtilities.GetNamespace(typeSymbol);
        var typeName = typeSymbol.Name;
        var containingTypes = GeneratorUtilities.GetContainingTypes(typeSymbol);

        // Get optional manual Id / Singleton flag from [Queryable(Id = X, Singleton = true)]
        int? manualId = null;
        bool isSingleton = false;
        foreach (var attr in context.Attributes)
        {
            foreach (var namedArg in attr.NamedArguments)
            {
                if (namedArg.Key == "Id" && namedArg.Value.Value is int idValue && idValue >= 0)
                {
                    manualId = idValue;
                }
                if (namedArg.Key == "Singleton" && namedArg.Value.Value is bool singleton)
                {
                    isSingleton = singleton;
                }
            }
        }

        // Collect component constraints from attributes
        // Track component -> list of attribute types for duplicate detection
        var componentUsages = new Dictionary<string, List<string>>();
        var withComponents = new List<string>();
        var withComponentsAccess = new List<ComponentInfo>();
        var withoutComponents = new List<string>();
        var anyComponents = new List<string>();
        var optionalComponents = new List<ComponentInfo>();
        var withTags = new List<string>();

        foreach (var attr in typeSymbol.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass is null) continue;

            // Check if it's a generic attribute
            if (attrClass.IsGenericType && attrClass.OriginalDefinition is { } originalDef)
            {
                var metadataName = originalDef.ToDisplayString();
                var typeArg = attrClass.TypeArguments.FirstOrDefault();
                if (typeArg is null) continue;

                var componentFullName = typeArg.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (componentFullName.StartsWith("global::", StringComparison.Ordinal))
                    componentFullName = componentFullName.Substring(8);

                // Get simple type name (without namespace)
                var componentTypeName = typeArg.Name;

                string? attrType = null;

                // Match by checking if it ends with the expected attribute name pattern
                if (metadataName.StartsWith("Paradise.ECS.WithAttribute<", StringComparison.Ordinal))
                {
                    withComponents.Add(componentFullName);
                    attrType = "With";

                    // Extract Name, IsReadOnly, QueryOnly from named arguments
                    string? customName = null;
                    bool isReadOnly = false;
                    bool queryOnly = false;

                    foreach (var namedArg in attr.NamedArguments)
                    {
                        switch (namedArg.Key)
                        {
                            case "Name" when namedArg.Value.Value is string name:
                                customName = name;
                                break;
                            case "IsReadOnly" when namedArg.Value.Value is bool ro:
                                isReadOnly = ro;
                                break;
                            case "QueryOnly" when namedArg.Value.Value is bool qo:
                                queryOnly = qo;
                                break;
                        }
                    }

                    withComponentsAccess.Add(new ComponentInfo(
                        componentFullName, componentTypeName, customName, isReadOnly, queryOnly));
                }
                else if (metadataName.StartsWith("Paradise.ECS.WithTagAttribute<", StringComparison.Ordinal))
                {
                    // Deliberately NOT added to componentUsages: a tag is not a component, so
                    // [With<X>] beside [WithTag<X>] is not the duplicate that check looks for —
                    // it cannot even be written, since the two attributes constrain to different
                    // interfaces.
                    withTags.Add(componentFullName);
                    attrType = null;
                }
                else if (metadataName.StartsWith("Paradise.ECS.WithoutAttribute<", StringComparison.Ordinal))
                {
                    withoutComponents.Add(componentFullName);
                    attrType = "Without";
                }
                else if (metadataName.StartsWith("Paradise.ECS.WithAnyAttribute<", StringComparison.Ordinal))
                {
                    anyComponents.Add(componentFullName);
                    attrType = "Any";
                }
                else if (metadataName.StartsWith("Paradise.ECS.OptionalAttribute<", StringComparison.Ordinal))
                {
                    attrType = "Optional";

                    // Extract Name, IsReadOnly from named arguments
                    string? customName = null;
                    bool isReadOnly = false;

                    foreach (var namedArg in attr.NamedArguments)
                    {
                        switch (namedArg.Key)
                        {
                            case "Name" when namedArg.Value.Value is string name:
                                customName = name;
                                break;
                            case "IsReadOnly" when namedArg.Value.Value is bool ro:
                                isReadOnly = ro;
                                break;
                        }
                    }

                    optionalComponents.Add(new ComponentInfo(
                        componentFullName, componentTypeName, customName, isReadOnly));
                }

                // Track usage for duplicate detection
                if (attrType != null)
                {
                    if (!componentUsages.TryGetValue(componentFullName, out var usages))
                    {
                        usages = new List<string>();
                        componentUsages[componentFullName] = usages;
                    }
                    usages.Add(attrType);
                }
            }
        }

        // Find duplicates
        var duplicates = componentUsages
            .Where(kvp => kvp.Value.Count > 1)
            .Select(kvp => (Component: kvp.Key, Attributes: kvp.Value))
            .ToImmutableArray();

        return new QueryableInfo(
            fullyQualifiedName,
            typeSymbol.Locations.FirstOrDefault() ?? Location.None,
            isRefStruct,
            isPartial,
            ns,
            typeName,
            containingTypes,
            manualId,
            isSingleton,
            withComponents.ToImmutableArray(),
            withComponentsAccess.ToImmutableArray(),
            withoutComponents.ToImmutableArray(),
            anyComponents.ToImmutableArray(),
            optionalComponents.ToImmutableArray(),
            withTags.ToImmutableArray(),
            duplicates);
    }

    private static void GenerateQueryableCode(
        SourceProductionContext context,
        ImmutableArray<QueryableInfo> queryables,
        int componentCount,
        bool suppressGlobalUsings,
        string? defaultConfigFQN,
        string rootNamespace)
    {
        if (queryables.IsEmpty)
            return;

        // Sort by fully qualified name for deterministic ID assignment
        var sorted = queryables
            .OrderBy(static q => q.FullyQualifiedName, StringComparer.Ordinal)
            .ToList();

        // Report diagnostics for invalid queryables
        foreach (var queryable in sorted)
        {
            if (!queryable.IsRefStruct)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.QueryableMustBeRefStruct,
                    queryable.Location,
                    queryable.FullyQualifiedName));
            }

            if (!queryable.IsPartial)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.QueryableMustBePartial,
                    queryable.Location,
                    queryable.FullyQualifiedName));
            }

            // Report duplicate component diagnostics
            foreach (var (component, attrs) in queryable.DuplicateComponents)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.DuplicateComponentInQueryable,
                    queryable.Location,
                    component,
                    queryable.FullyQualifiedName,
                    string.Join(", ", attrs)));
            }
        }

        // Filter to valid queryables (must be ref struct, partial, and no duplicates)
        var validQueryables = sorted.Where(q => q.IsRefStruct && q.IsPartial && !q.HasDuplicates).ToList();
        if (validQueryables.Count == 0)
            return;

        // Detect duplicate manual IDs
        var manualIdGroups = validQueryables
            .Where(q => q.ManualId.HasValue)
            .GroupBy(q => q.ManualId!.Value)
            .Where(g => g.Count() > 1)
            .ToList();

        // Report diagnostics for duplicate manual IDs
        var duplicateManualIds = new HashSet<int>();
        foreach (var group in manualIdGroups)
        {
            duplicateManualIds.Add(group.Key);
            var typeNames = string.Join(", ", group.Select(q => q.FullyQualifiedName));
            // Report on the first occurrence's location
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.DuplicateQueryableId,
                group.First().Location,
                group.Key,
                typeNames));
        }

        // Filter out queryables with duplicate manual IDs
        validQueryables = validQueryables
            .Where(q => !q.ManualId.HasValue || !duplicateManualIds.Contains(q.ManualId.Value))
            .ToList();
        if (validQueryables.Count == 0)
            return;

        // Assign IDs (manual first, then auto-assign)
        var manualIds = new HashSet<int>(validQueryables
            .Where(q => q.ManualId.HasValue)
            .Select(q => q.ManualId!.Value));

        var queryableWithIds = new List<(QueryableInfo Info, int TypeId)>();
        int nextAutoId = 0;

        foreach (var queryable in validQueryables)
        {
            if (queryable.ManualId.HasValue)
            {
                queryableWithIds.Add((queryable, queryable.ManualId.Value));
            }
            else
            {
                while (manualIds.Contains(nextAutoId)) nextAutoId++;
                queryableWithIds.Add((queryable, nextAutoId));
                nextAutoId++;
            }
        }

        // Compute mask/config types for aliases
        var maskTypeFullyQualified = GeneratorUtilities.GetOptimalMaskType(componentCount);
        var configTypeFull = $"global::{defaultConfigFQN ?? "Paradise.ECS.DefaultConfig"}";

        // Generate partial struct implementations with TypeId
        foreach (var (info, typeId) in queryableWithIds)
        {
            GeneratePartialStruct(context, info, typeId, maskTypeFullyQualified, configTypeFull, rootNamespace);
        }

        // Generate QueryableRegistry
        GenerateQueryableRegistry(context, queryableWithIds, componentCount, suppressGlobalUsings);
    }

    private static void GeneratePartialStruct(
        SourceProductionContext context,
        QueryableInfo queryable,
        int typeId,
        string maskTypeFullyQualified,
        string configTypeFull,
        string rootNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Paradise.ECS;");  // Required for extension methods like GetRef<T>
        sb.AppendLine();

        // Use block-scoped namespace for consistency (required since QueryBuilder and Query are in same file)
        var hasNamespace = queryable.Namespace != null;
        var baseIndent = hasNamespace ? "    " : "";

        if (hasNamespace)
        {
            sb.AppendLine($"namespace {queryable.Namespace}");
            sb.AppendLine("{");
        }

        // Open containing types if nested
        var indent = baseIndent;
        foreach (var containingType in queryable.ContainingTypes)
        {
            sb.AppendLine($"{indent}partial {containingType.Keyword} {containingType.Name}");
            sb.AppendLine($"{indent}{{");
            indent += "    ";
        }

        // Generate the partial ref struct with QueryableId and Query/ChunkQuery static methods.
        // IComponentSet lets an entity be BUILT from this queryable (EntityBuilder.EnsureFrom),
        // so the archetype follows what the systems query for instead of a parallel hand-written
        // component list that can silently drift out of it.
        sb.AppendLine($"{indent}partial struct {queryable.TypeName} : global::Paradise.ECS.IComponentSet");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    /// <summary>The unique queryable type ID.</summary>");
        sb.AppendLine($"{indent}    public static int QueryableId => {typeId};");
        sb.AppendLine();

        GenerateCollectComponentTypes(sb, queryable, indent + "    ", rootNamespace);

        // Generate static Query method
        sb.AppendLine($"{indent}    /// <summary>Builds a query that iterates over {queryable.TypeName}.Data instances.</summary>");
        sb.AppendLine($"{indent}    /// <typeparam name=\"TWorld\">The world type implementing IWorld.</typeparam>");
        sb.AppendLine($"{indent}    /// <typeparam name=\"TMask\">The component mask type implementing IBitSet.</typeparam>");
        sb.AppendLine($"{indent}    /// <typeparam name=\"TConfig\">The world configuration type.</typeparam>");
        sb.AppendLine($"{indent}    /// <param name=\"world\">The world to query.</param>");
        sb.AppendLine($"{indent}    /// <returns>A query result that iterates over {queryable.TypeName}.Data instances.</returns>");
        sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}    public static global::Paradise.ECS.QueryResult<Data<TMask, TConfig>, global::Paradise.ECS.Archetype<TMask, TConfig>, TMask, TConfig> Query<TWorld, TMask, TConfig>(");
        sb.AppendLine($"{indent}        TWorld world)");
        sb.AppendLine($"{indent}        where TWorld : global::Paradise.ECS.IWorld<TMask, TConfig>");
        sb.AppendLine($"{indent}        where TMask : unmanaged, global::Paradise.ECS.IBitSet<TMask>");
        sb.AppendLine($"{indent}        where TConfig : global::Paradise.ECS.IConfig, new()");
        sb.AppendLine($"{indent}        => global::Paradise.ECS.QueryHelpers.CreateQueryResult<Data<TMask, TConfig>, TMask, TConfig>(world, global::Paradise.ECS.QueryableRegistry<TMask>.Descriptions[QueryableId]);");
        sb.AppendLine();

        // Generate static ChunkQuery method
        sb.AppendLine($"{indent}    /// <summary>Builds a chunk query that iterates over {queryable.TypeName}.ChunkData instances for batch processing.</summary>");
        sb.AppendLine($"{indent}    /// <typeparam name=\"TWorld\">The world type implementing IWorld.</typeparam>");
        sb.AppendLine($"{indent}    /// <typeparam name=\"TMask\">The component mask type implementing IBitSet.</typeparam>");
        sb.AppendLine($"{indent}    /// <typeparam name=\"TConfig\">The world configuration type.</typeparam>");
        sb.AppendLine($"{indent}    /// <param name=\"world\">The world to query.</param>");
        sb.AppendLine($"{indent}    /// <returns>A chunk query result that iterates over {queryable.TypeName}.ChunkData instances.</returns>");
        sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}    public static global::Paradise.ECS.ChunkQueryResult<ChunkData<TMask, TConfig>, global::Paradise.ECS.Archetype<TMask, TConfig>, TMask, TConfig> ChunkQuery<TWorld, TMask, TConfig>(");
        sb.AppendLine($"{indent}        TWorld world)");
        sb.AppendLine($"{indent}        where TWorld : global::Paradise.ECS.IWorld<TMask, TConfig>");
        sb.AppendLine($"{indent}        where TMask : unmanaged, global::Paradise.ECS.IBitSet<TMask>");
        sb.AppendLine($"{indent}        where TConfig : global::Paradise.ECS.IConfig, new()");
        sb.AppendLine($"{indent}        => global::Paradise.ECS.QueryHelpers.CreateChunkQueryResult<ChunkData<TMask, TConfig>, TMask, TConfig>(world, global::Paradise.ECS.QueryableRegistry<TMask>.Descriptions[QueryableId]);");
        sb.AppendLine();

        // Generate nested Data<TMask, TConfig> struct implementing IQueryData
        GenerateNestedDataStruct(sb, queryable, indent + "    ", rootNamespace);

        // Generate a readonly arbitrary-entity view that never exposes writable refs.
        GenerateNestedReadDataStruct(sb, queryable, indent + "    ");

        // Generate arbitrary-entity accessors for systems that need a target by handle.
        GenerateNestedEntityAccessorStructs(sb, queryable, indent + "    ");

        // Generate nested ChunkData<TMask, TConfig> struct implementing IQueryChunkData
        GenerateNestedChunkDataStruct(sb, queryable, indent + "    ");

        // Generate nested Segments<TMask, TConfig> struct for world systems
        GenerateNestedSegmentsStruct(sb, queryable, indent + "    ");

        // Generate nested Singleton<TMask, TConfig> struct for [Queryable(Singleton = true)]
        if (queryable.IsSingleton)
        {
            GenerateNestedSingletonStruct(sb, queryable, indent + "    ");
        }

        // Default-config views: PlayerAvatar.Entity, CameraFrame.Singleton, … — the types
        // system fields declare. They wrap the generic nested types with the project's
        // mask/config baked in, so Execute can write Avatar.WalkIntent without type args.
        GenerateDefaultConfigViews(sb, queryable, indent + "    ", maskTypeFullyQualified, configTypeFull);

        sb.AppendLine($"{indent}}}");

        // Close containing types
        for (int i = queryable.ContainingTypes.Length - 1; i >= 0; i--)
        {
            indent = baseIndent + new string(' ', i * 4);
            sb.AppendLine($"{indent}}}");
        }

        // Close namespace
        if (hasNamespace)
        {
            sb.AppendLine("}");
        }

        // Generate extension methods in Paradise.ECS namespace
        sb.AppendLine();
        GenerateQueryableExtensionMethods(sb, queryable);

        // Generate filename
        var filename = "Queryable_" + queryable.FullyQualifiedName.Replace(".", "_").Replace("+", "_") + ".g.cs";
        context.AddSource(filename, sb.ToString());
    }

    private static void GenerateNestedEntityAccessorStructs(StringBuilder sb, QueryableInfo queryable, string indent)
    {
        sb.AppendLine();
        sb.AppendLine($"{indent}// ===================== Arbitrary Entity Access =====================");

        GenerateNestedEntityAccessor(sb, queryable, indent, "ReadLookup", reader: true);
        GenerateNestedEntityAccessor(sb, queryable, indent, "WriteLookup", reader: false);
    }

    private static void GenerateNestedEntityAccessor(
        StringBuilder sb,
        QueryableInfo queryable,
        string indent,
        string typeName,
        bool reader)
    {
        sb.AppendLine();
        sb.AppendLine($"{indent}/// <summary>{(reader ? "Read-only" : "Read/write")} handle lookup into matching {queryable.TypeName} entities.</summary>");
        sb.AppendLine($"{indent}/// <typeparam name=\"TMask\">The component mask type implementing IBitSet.</typeparam>");
        sb.AppendLine($"{indent}/// <typeparam name=\"TConfig\">The world configuration type.</typeparam>");
        sb.AppendLine($"{indent}public readonly ref struct {typeName}<TMask, TConfig>");
        sb.AppendLine($"{indent}    where TMask : unmanaged, global::Paradise.ECS.IBitSet<TMask>");
        sb.AppendLine($"{indent}    where TConfig : global::Paradise.ECS.IConfig, new()");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    private readonly global::Paradise.ECS.IWorld<TMask, TConfig> _world;");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public {typeName}(global::Paradise.ECS.IWorld<TMask, TConfig> world) => _world = world;");
        sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>Returns whether the entity is alive and matches {queryable.TypeName}.</summary>");
        sb.AppendLine($"{indent}    public bool Has(global::Paradise.ECS.Entity entity) => TryGet(entity, out _);");
        sb.AppendLine();
        var dataTypeName = reader
            ? $"{queryable.TypeName}.ReadData<TMask, TConfig>"
            : $"{queryable.TypeName}.Data<TMask, TConfig>";
        sb.AppendLine($"{indent}    /// <summary>Returns a live component view when the entity matches.</summary>");
        sb.AppendLine($"{indent}    public bool TryGet(global::Paradise.ECS.Entity entity, out {dataTypeName} data)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        data = default;");
        sb.AppendLine($"{indent}        if (entity.IsPlaceholder || !_world.IsAlive(entity)) return false;");
        sb.AppendLine();
        sb.AppendLine($"{indent}        var location = _world.EntityManager.GetLocation(entity.Id);");
        sb.AppendLine($"{indent}        if (!location.MatchesEntity(entity)) return false;");
        sb.AppendLine();
        sb.AppendLine($"{indent}        var archetype = _world.ArchetypeRegistry.GetById(location.ArchetypeId);");
        sb.AppendLine($"{indent}        if (archetype is null) return false;");
        sb.AppendLine();
        sb.AppendLine($"{indent}        var description = global::Paradise.ECS.QueryableRegistry<TMask>.Descriptions[QueryableId];");
        sb.AppendLine($"{indent}        if (!description.Value.Matches(archetype.Layout.ComponentMask)) return false;");
        sb.AppendLine();
        sb.AppendLine($"{indent}        var (chunkIndex, indexInChunk) = archetype.GetChunkLocation(location.GlobalIndex);");
        sb.AppendLine($"{indent}        var chunk = archetype.GetChunk(chunkIndex);");
        sb.AppendLine($"{indent}        if (!global::Paradise.ECS.QueryHelpers.RowMatches<{queryable.TypeName}.Data<TMask, TConfig>, TMask, TConfig>(_world.ChunkManager, archetype.Layout, chunk, indexInChunk)) return false;");
        sb.AppendLine();
        if (reader)
        {
            sb.AppendLine($"{indent}        data = new {queryable.TypeName}.ReadData<TMask, TConfig>(");
            sb.AppendLine($"{indent}            _world.ChunkManager, archetype.Layout, chunk, indexInChunk);");
        }
        else
        {
            sb.AppendLine($"{indent}        data = {queryable.TypeName}.Data<TMask, TConfig>.CreateSnapshot(");
            sb.AppendLine($"{indent}            _world.ChunkManager, archetype.Layout, chunk,");
            sb.AppendLine($"{indent}            _world.ChunkManager, chunk, indexInChunk);");
        }
        sb.AppendLine($"{indent}        return true;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
    }

    private static void GenerateNestedReadDataStruct(StringBuilder sb, QueryableInfo queryable, string indent)
    {
        sb.AppendLine();
        sb.AppendLine($"{indent}/// <summary>Read-only iteration data for arbitrary-entity access.</summary>");
        sb.AppendLine($"{indent}public readonly ref struct ReadData<TMask, TConfig>");
        sb.AppendLine($"{indent}    where TMask : unmanaged, global::Paradise.ECS.IBitSet<TMask>");
        sb.AppendLine($"{indent}    where TConfig : global::Paradise.ECS.IConfig, new()");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    private readonly global::Paradise.ECS.ChunkManager _chunkManager;");
        sb.AppendLine($"{indent}    private readonly nint _layoutData;");
        sb.AppendLine($"{indent}    private readonly global::Paradise.ECS.ChunkHandle _chunk;");
        sb.AppendLine($"{indent}    private readonly int _indexInChunk;");
        sb.AppendLine();
        sb.AppendLine($"{indent}    internal ReadData(");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager chunkManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig> layout,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkHandle chunk,");
        sb.AppendLine($"{indent}        int indexInChunk)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        _chunkManager = chunkManager;");
        sb.AppendLine($"{indent}        _layoutData = layout.DataPointer;");
        sb.AppendLine($"{indent}        _chunk = chunk;");
        sb.AppendLine($"{indent}        _indexInChunk = indexInChunk;");
        sb.AppendLine($"{indent}    }}");

        foreach (var comp in queryable.WithComponentsAccess)
        {
            if (comp.QueryOnly) continue;
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>Gets a read-only reference to the {comp.ComponentTypeName} component.</summary>");
            sb.AppendLine($"{indent}    public ref readonly global::{comp.ComponentFullName} {comp.PropertyName}");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"{indent}        get");
            sb.AppendLine($"{indent}        {{");
            sb.AppendLine($"{indent}            int offset = new global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig>(_layoutData).GetBaseOffset(global::{comp.ComponentFullName}.TypeId) + _indexInChunk * global::{comp.ComponentFullName}.Size;");
            sb.AppendLine($"{indent}            return ref _chunkManager.GetBytes(_chunk).GetRef<global::{comp.ComponentFullName}>(offset);");
            sb.AppendLine($"{indent}        }}");
            sb.AppendLine($"{indent}    }}");
        }

        foreach (var opt in queryable.OptionalComponents)
        {
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>Gets whether the {opt.ComponentTypeName} component is present.</summary>");
            sb.AppendLine($"{indent}    public bool Has{opt.PropertyName}");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"{indent}        get => new global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig>(_layoutData).HasComponent(global::{opt.ComponentFullName}.TypeId);");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>Gets a read-only reference to the {opt.ComponentTypeName} component.</summary>");
            sb.AppendLine($"{indent}    /// <exception cref=\"global::System.InvalidOperationException\">Thrown when the component is not present. Check Has{opt.PropertyName} first.</exception>");
            sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"{indent}    public ref readonly global::{opt.ComponentFullName} Get{opt.PropertyName}()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        int baseOffset = new global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig>(_layoutData).GetBaseOffset(global::{opt.ComponentFullName}.TypeId);");
            sb.AppendLine($"{indent}        if (baseOffset < 0)");
            sb.AppendLine($"{indent}            throw new global::System.InvalidOperationException(\"Optional component {opt.ComponentTypeName} is not present. Check Has{opt.PropertyName} before calling Get{opt.PropertyName}().\");");
            sb.AppendLine($"{indent}        int offset = baseOffset + _indexInChunk * global::{opt.ComponentFullName}.Size;");
            sb.AppendLine($"{indent}        return ref _chunkManager.GetBytes(_chunk).GetRef<global::{opt.ComponentFullName}>(offset);");
            sb.AppendLine($"{indent}    }}");
        }

        sb.AppendLine($"{indent}}}");
    }

    private static void GenerateQueryableExtensionMethods(StringBuilder sb, QueryableInfo queryable)
    {
        var queryableName = queryable.TypeName;
        var fullyQualifiedName = "global::" + queryable.FullyQualifiedName.Replace("+", ".");

        sb.AppendLine("namespace Paradise.ECS");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>Extension methods for querying {queryableName} entities.</summary>");
        sb.AppendLine($"    public static class {queryable.HelperStructPrefix}QueryableExtensions");
        sb.AppendLine("    {");

        // Generate Query extension method
        sb.AppendLine($"        /// <summary>Queries for {queryableName} entities using entity-level iteration.</summary>");
        sb.AppendLine($"        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"        public static global::Paradise.ECS.QueryResult<{fullyQualifiedName}.Data<TMask, TConfig>, global::Paradise.ECS.Archetype<TMask, TConfig>, TMask, TConfig> Query<TMask, TConfig>(");
        sb.AppendLine($"            this global::Paradise.ECS.IWorld<TMask, TConfig> world, {fullyQualifiedName} selector)");
        sb.AppendLine($"            where TMask : unmanaged, global::Paradise.ECS.IBitSet<TMask> where TConfig : global::Paradise.ECS.IConfig, new()");
        sb.AppendLine($"            => global::Paradise.ECS.QueryHelpers.CreateQueryResult<{fullyQualifiedName}.Data<TMask, TConfig>, TMask, TConfig>(world, global::Paradise.ECS.QueryableRegistry<TMask>.Descriptions[{fullyQualifiedName}.QueryableId]);");
        sb.AppendLine();

        // Generate ChunkQuery extension method
        sb.AppendLine($"        /// <summary>Queries for {queryableName} entities using chunk-level iteration.</summary>");
        sb.AppendLine($"        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"        public static global::Paradise.ECS.ChunkQueryResult<{fullyQualifiedName}.ChunkData<TMask, TConfig>, global::Paradise.ECS.Archetype<TMask, TConfig>, TMask, TConfig> ChunkQuery<TMask, TConfig>(");
        sb.AppendLine($"            this global::Paradise.ECS.IWorld<TMask, TConfig> world, {fullyQualifiedName} selector)");
        sb.AppendLine($"            where TMask : unmanaged, global::Paradise.ECS.IBitSet<TMask> where TConfig : global::Paradise.ECS.IConfig, new()");
        sb.AppendLine($"            => global::Paradise.ECS.QueryHelpers.CreateChunkQueryResult<{fullyQualifiedName}.ChunkData<TMask, TConfig>, TMask, TConfig>(world, global::Paradise.ECS.QueryableRegistry<TMask>.Descriptions[{fullyQualifiedName}.QueryableId]);");

        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    private static void GenerateNestedDataStruct(
        StringBuilder sb, QueryableInfo queryable, string indent, string rootNamespace)
    {
        sb.AppendLine($"{indent}/// <summary>");
        sb.AppendLine($"{indent}/// Iteration data providing component access. Returned by query enumeration.");
        sb.AppendLine($"{indent}/// </summary>");
        sb.AppendLine($"{indent}/// <typeparam name=\"TMask\">The component mask type implementing IBitSet.</typeparam>");
        sb.AppendLine($"{indent}/// <typeparam name=\"TConfig\">The world configuration type.</typeparam>");
        sb.AppendLine($"{indent}public readonly ref struct Data<TMask, TConfig>");
        sb.AppendLine($"{indent}    : global::Paradise.ECS.IQueryData<Data<TMask, TConfig>, TMask, TConfig>");
        sb.AppendLine($"{indent}    where TMask : unmanaged, global::Paradise.ECS.IBitSet<TMask>");
        sb.AppendLine($"{indent}    where TConfig : global::Paradise.ECS.IConfig, new()");
        sb.AppendLine($"{indent}{{");

        // Generate private fields. Read-only components bind to the READ chunk (== the write
        // chunk except under snapshot-read execution, where it is the previous-tick pair) so
        // mixed writable/read-only compositions never read in-flight writes.
        sb.AppendLine($"{indent}    private readonly global::Paradise.ECS.ChunkManager _chunkManager;");
        sb.AppendLine($"{indent}    private readonly nint _layoutData;");
        sb.AppendLine($"{indent}    private readonly global::Paradise.ECS.ChunkHandle _chunk;");
        sb.AppendLine($"{indent}    private readonly global::Paradise.ECS.ChunkManager _readChunkManager;");
        sb.AppendLine($"{indent}    private readonly global::Paradise.ECS.ChunkHandle _readChunk;");
        sb.AppendLine($"{indent}    private readonly int _indexInChunk;");
        sb.AppendLine();

        // Generate static Create method (required by IQueryData)
        sb.AppendLine($"{indent}    /// <summary>Creates a new Data instance. Required by IQueryData interface.</summary>");
        sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}    public static Data<TMask, TConfig> Create(");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager chunkManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.IEntityManager entityManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig> layout,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkHandle chunk,");
        sb.AppendLine($"{indent}        int indexInChunk)");
        sb.AppendLine($"{indent}        => new(chunkManager, layout, chunk, chunkManager, chunk, indexInChunk);");
        sb.AppendLine();

        // Snapshot-read factory: read-only component properties bind to the paired read chunk.
        sb.AppendLine($"{indent}    /// <summary>Creates a Data instance whose read-only components bind to the paired");
        sb.AppendLine($"{indent}    /// READ chunk (snapshot-read execution); writable components bind to the write chunk.</summary>");
        sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}    public static Data<TMask, TConfig> CreateSnapshot(");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager chunkManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig> layout,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkHandle chunk,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager readChunkManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkHandle readChunk,");
        sb.AppendLine($"{indent}        int indexInChunk)");
        sb.AppendLine($"{indent}        => new(chunkManager, layout, chunk, readChunkManager, readChunk, indexInChunk);");
        sb.AppendLine();

        // Generate internal constructor
        sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}    internal Data(");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager chunkManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig> layout,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkHandle chunk,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager readChunkManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkHandle readChunk,");
        sb.AppendLine($"{indent}        int indexInChunk)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        _chunkManager = chunkManager;");
        sb.AppendLine($"{indent}        _layoutData = layout.DataPointer;");
        sb.AppendLine($"{indent}        _chunk = chunk;");
        sb.AppendLine($"{indent}        _readChunkManager = readChunkManager;");
        sb.AppendLine($"{indent}        _readChunk = readChunk;");
        sb.AppendLine($"{indent}        _indexInChunk = indexInChunk;");
        sb.AppendLine($"{indent}    }}");

        GenerateRowFilter(sb, queryable, indent + "    ", rootNamespace);

        // Generate component properties for With<T> components (unless QueryOnly)
        foreach (var comp in queryable.WithComponentsAccess)
        {
            if (comp.QueryOnly)
                continue;

            sb.AppendLine();
            var refType = comp.IsReadOnly ? "ref readonly" : "ref";
            sb.AppendLine($"{indent}    /// <summary>Gets a {(comp.IsReadOnly ? "read-only " : "")}reference to the {comp.ComponentTypeName} component.</summary>");
            sb.AppendLine($"{indent}    public {refType} global::{comp.ComponentFullName} {comp.PropertyName}");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"{indent}        get");
            sb.AppendLine($"{indent}        {{");
            var (compManager, compChunk) = comp.IsReadOnly ? ("_readChunkManager", "_readChunk") : ("_chunkManager", "_chunk");
            sb.AppendLine($"{indent}            int offset = new global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig>(_layoutData).GetBaseOffset(global::{comp.ComponentFullName}.TypeId) + _indexInChunk * global::{comp.ComponentFullName}.Size;");
            sb.AppendLine($"{indent}            return ref {compManager}.GetBytes({compChunk}).GetRef<global::{comp.ComponentFullName}>(offset);");
            sb.AppendLine($"{indent}        }}");
            sb.AppendLine($"{indent}    }}");
        }

        // Generate HasXxx property and GetXxx() method for Optional<T> components
        foreach (var opt in queryable.OptionalComponents)
        {
            sb.AppendLine();
            // HasXxx property
            sb.AppendLine($"{indent}    /// <summary>Gets whether the {opt.ComponentTypeName} component is present.</summary>");
            sb.AppendLine($"{indent}    public bool Has{opt.PropertyName}");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"{indent}        get => new global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig>(_layoutData).HasComponent(global::{opt.ComponentFullName}.TypeId);");
            sb.AppendLine($"{indent}    }}");

            sb.AppendLine();
            // GetXxx() method
            var refType = opt.IsReadOnly ? "ref readonly" : "ref";
            sb.AppendLine($"{indent}    /// <summary>Gets a {(opt.IsReadOnly ? "read-only " : "")}reference to the {opt.ComponentTypeName} component.</summary>");
            sb.AppendLine($"{indent}    /// <exception cref=\"global::System.InvalidOperationException\">Thrown when the component is not present. Check Has{opt.PropertyName} first.</exception>");
            sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"{indent}    public {refType} global::{opt.ComponentFullName} Get{opt.PropertyName}()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        int baseOffset = new global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig>(_layoutData).GetBaseOffset(global::{opt.ComponentFullName}.TypeId);");
            sb.AppendLine($"{indent}        if (baseOffset < 0)");
            sb.AppendLine($"{indent}            throw new global::System.InvalidOperationException(\"Optional component {opt.ComponentTypeName} is not present. Check Has{opt.PropertyName} before calling Get{opt.PropertyName}().\");");
            var (optManager, optChunk) = opt.IsReadOnly ? ("_readChunkManager", "_readChunk") : ("_chunkManager", "_chunk");
            sb.AppendLine($"{indent}        int offset = baseOffset + _indexInChunk * global::{opt.ComponentFullName}.Size;");
            sb.AppendLine($"{indent}        return ref {optManager}.GetBytes({optChunk}).GetRef<global::{opt.ComponentFullName}>(offset);");
            sb.AppendLine($"{indent}    }}");
        }

        sb.AppendLine($"{indent}}}");
    }

    private static void GenerateNestedChunkDataStruct(StringBuilder sb, QueryableInfo queryable, string indent)
    {
        sb.AppendLine();
        sb.AppendLine($"{indent}/// <summary>");
        sb.AppendLine($"{indent}/// Chunk data providing span-based component access for batch processing.");
        sb.AppendLine($"{indent}/// </summary>");
        sb.AppendLine($"{indent}/// <typeparam name=\"TMask\">The component mask type implementing IBitSet.</typeparam>");
        sb.AppendLine($"{indent}/// <typeparam name=\"TConfig\">The world configuration type.</typeparam>");
        sb.AppendLine($"{indent}public readonly ref struct ChunkData<TMask, TConfig>");
        sb.AppendLine($"{indent}    : global::Paradise.ECS.IQueryChunkData<ChunkData<TMask, TConfig>, TMask, TConfig>");
        sb.AppendLine($"{indent}    where TMask : unmanaged, global::Paradise.ECS.IBitSet<TMask>");
        sb.AppendLine($"{indent}    where TConfig : global::Paradise.ECS.IConfig, new()");
        sb.AppendLine($"{indent}{{");

        // Generate private fields. Read-only spans bind to the READ chunk (== the write chunk
        // except under snapshot-read execution) so mixed compositions never read in-flight writes.
        sb.AppendLine($"{indent}    private readonly global::Paradise.ECS.ChunkManager _chunkManager;");
        sb.AppendLine($"{indent}    private readonly nint _layoutData;");
        sb.AppendLine($"{indent}    private readonly global::Paradise.ECS.ChunkHandle _chunk;");
        sb.AppendLine($"{indent}    private readonly global::Paradise.ECS.ChunkManager _readChunkManager;");
        sb.AppendLine($"{indent}    private readonly global::Paradise.ECS.ChunkHandle _readChunk;");
        sb.AppendLine($"{indent}    private readonly int _entityCount;");
        sb.AppendLine();

        // Generate static Create method (required by IQueryChunkData)
        sb.AppendLine($"{indent}    /// <summary>Creates a new ChunkData instance. Required by IQueryChunkData interface.</summary>");
        sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}    public static ChunkData<TMask, TConfig> Create(");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager chunkManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.IEntityManager entityManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig> layout,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkHandle chunk,");
        sb.AppendLine($"{indent}        int entityCount)");
        sb.AppendLine($"{indent}        => new(chunkManager, layout, chunk, chunkManager, chunk, entityCount);");
        sb.AppendLine();

        // Snapshot-read factory: read-only span properties bind to the paired read chunk.
        sb.AppendLine($"{indent}    /// <summary>Creates a ChunkData instance whose read-only spans bind to the paired");
        sb.AppendLine($"{indent}    /// READ chunk (snapshot-read execution); writable spans bind to the write chunk.</summary>");
        sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}    public static ChunkData<TMask, TConfig> CreateSnapshot(");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager chunkManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig> layout,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkHandle chunk,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager readChunkManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkHandle readChunk,");
        sb.AppendLine($"{indent}        int entityCount)");
        sb.AppendLine($"{indent}        => new(chunkManager, layout, chunk, readChunkManager, readChunk, entityCount);");
        sb.AppendLine();

        // Generate internal constructor
        sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}    internal ChunkData(");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager chunkManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig> layout,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkHandle chunk,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager readChunkManager,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkHandle readChunk,");
        sb.AppendLine($"{indent}        int entityCount)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        _chunkManager = chunkManager;");
        sb.AppendLine($"{indent}        _layoutData = layout.DataPointer;");
        sb.AppendLine($"{indent}        _chunk = chunk;");
        sb.AppendLine($"{indent}        _readChunkManager = readChunkManager;");
        sb.AppendLine($"{indent}        _readChunk = readChunk;");
        sb.AppendLine($"{indent}        _entityCount = entityCount;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();

        // EntityCount property
        sb.AppendLine($"{indent}    /// <summary>Gets the number of entities in this chunk.</summary>");
        sb.AppendLine($"{indent}    public int EntityCount");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}        get => _entityCount;");
        sb.AppendLine($"{indent}    }}");

        // Generate span properties for With<T> components (unless QueryOnly)
        foreach (var comp in queryable.WithComponentsAccess)
        {
            if (comp.QueryOnly)
                continue;

            sb.AppendLine();
            // Pluralize property name for span (simple pluralization)
            var spanPropertyName = comp.PropertyName + "Span";
            var spanType = comp.IsReadOnly ? "ReadOnlySpan" : "Span";
            sb.AppendLine($"{indent}    /// <summary>Gets a {(comp.IsReadOnly ? "read-only " : "")}span over all {comp.ComponentTypeName} components in this chunk.</summary>");
            sb.AppendLine($"{indent}    public global::System.{spanType}<global::{comp.ComponentFullName}> {spanPropertyName}");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"{indent}        get");
            sb.AppendLine($"{indent}        {{");
            var (compManager, compChunk) = comp.IsReadOnly ? ("_readChunkManager", "_readChunk") : ("_chunkManager", "_chunk");
            sb.AppendLine($"{indent}            int baseOffset = new global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig>(_layoutData).GetBaseOffset(global::{comp.ComponentFullName}.TypeId);");
            sb.AppendLine($"{indent}            return {compManager}.GetBytes({compChunk}).GetSpan<global::{comp.ComponentFullName}>(baseOffset, _entityCount);");
            sb.AppendLine($"{indent}        }}");
            sb.AppendLine($"{indent}    }}");
        }

        // Generate Has property and GetXxxSpan() method for Optional<T> components
        foreach (var opt in queryable.OptionalComponents)
        {
            sb.AppendLine();
            // HasXxx property
            sb.AppendLine($"{indent}    /// <summary>Gets whether this chunk's archetype has the {opt.ComponentTypeName} component.</summary>");
            sb.AppendLine($"{indent}    public bool Has{opt.PropertyName}");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"{indent}        get => new global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig>(_layoutData).HasComponent(global::{opt.ComponentFullName}.TypeId);");
            sb.AppendLine($"{indent}    }}");

            sb.AppendLine();
            // GetXxxSpan() method - pluralize name
            var spanMethodName = "Get" + opt.PropertyName + "Span";
            var optSpanType = opt.IsReadOnly ? "ReadOnlySpan" : "Span";
            sb.AppendLine($"{indent}    /// <summary>Gets a {(opt.IsReadOnly ? "read-only " : "")}span over all {opt.ComponentTypeName} components in this chunk.</summary>");
            sb.AppendLine($"{indent}    /// <exception cref=\"global::System.InvalidOperationException\">Thrown when the component is not present. Check Has{opt.PropertyName} first.</exception>");
            sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"{indent}    public global::System.{optSpanType}<global::{opt.ComponentFullName}> {spanMethodName}()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        int baseOffset = new global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig>(_layoutData).GetBaseOffset(global::{opt.ComponentFullName}.TypeId);");
            sb.AppendLine($"{indent}        if (baseOffset < 0)");
            var (optManager, optChunk) = opt.IsReadOnly ? ("_readChunkManager", "_readChunk") : ("_chunkManager", "_chunk");
            sb.AppendLine($"{indent}            throw new global::System.InvalidOperationException(\"Optional component {opt.ComponentTypeName} is not present. Check Has{opt.PropertyName} before calling {spanMethodName}().\");");
            sb.AppendLine($"{indent}        return {optManager}.GetBytes({optChunk}).GetSpan<global::{opt.ComponentFullName}>(baseOffset, _entityCount);");
            sb.AppendLine($"{indent}    }}");
        }

        sb.AppendLine($"{indent}}}");
    }

    /// <summary>
    /// Generates the nested Segments struct: whole-query flat component views for world systems
    /// (<c>IWorldSystem</c>). Writable components bind to the WRITE segment table, read-only
    /// components to the READ table (identical tables under classic execution; the read table
    /// points at the snapshot world's paired chunks under snapshot-read execution).
    /// </summary>
    private static void GenerateNestedSegmentsStruct(StringBuilder sb, QueryableInfo queryable, string indent)
    {
        sb.AppendLine();
        sb.AppendLine($"{indent}/// <summary>");
        sb.AppendLine($"{indent}/// Whole-query segment views for world systems: flat, index-correlated access to every");
        sb.AppendLine($"{indent}/// matching entity across all chunks.");
        sb.AppendLine($"{indent}/// </summary>");
        sb.AppendLine($"{indent}/// <typeparam name=\"TMask\">The component mask type implementing IBitSet.</typeparam>");
        sb.AppendLine($"{indent}/// <typeparam name=\"TConfig\">The world configuration type.</typeparam>");
        sb.AppendLine($"{indent}public readonly ref struct Segments<TMask, TConfig>");
        sb.AppendLine($"{indent}    where TMask : unmanaged, global::Paradise.ECS.IBitSet<TMask>");
        sb.AppendLine($"{indent}    where TConfig : global::Paradise.ECS.IConfig, new()");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    private readonly global::Paradise.ECS.ChunkManager _chunkManager;");
        sb.AppendLine($"{indent}    private readonly global::System.ReadOnlySpan<global::Paradise.ECS.ComponentSegment> _writeSegments;");
        sb.AppendLine($"{indent}    private readonly global::System.ReadOnlySpan<global::Paradise.ECS.ComponentSegment> _readSegments;");
        sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>Creates segment views over pre-built chunk tables (write + read).</summary>");
        sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}    public static Segments<TMask, TConfig> Create(");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager chunkManager,");
        sb.AppendLine($"{indent}        global::System.ReadOnlySpan<global::Paradise.ECS.ComponentSegment> writeSegments,");
        sb.AppendLine($"{indent}        global::System.ReadOnlySpan<global::Paradise.ECS.ComponentSegment> readSegments)");
        sb.AppendLine($"{indent}        => new(chunkManager, writeSegments, readSegments);");
        sb.AppendLine();
        sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}    internal Segments(");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkManager chunkManager,");
        sb.AppendLine($"{indent}        global::System.ReadOnlySpan<global::Paradise.ECS.ComponentSegment> writeSegments,");
        sb.AppendLine($"{indent}        global::System.ReadOnlySpan<global::Paradise.ECS.ComponentSegment> readSegments)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        _chunkManager = chunkManager;");
        sb.AppendLine($"{indent}        _writeSegments = writeSegments;");
        sb.AppendLine($"{indent}        _readSegments = readSegments;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>Total entity count across all matching chunks.</summary>");
        sb.AppendLine($"{indent}    public int Length");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}        get => _writeSegments.Length == 0 ? 0 : _writeSegments[^1].Start + _writeSegments[^1].Count;");
        sb.AppendLine($"{indent}    }}");

        foreach (var comp in queryable.WithComponentsAccess)
        {
            if (comp.QueryOnly)
                continue;

            sb.AppendLine();
            if (comp.IsReadOnly)
            {
                sb.AppendLine($"{indent}    /// <summary>Read-only flat view over all {comp.ComponentTypeName} components (READ table).</summary>");
                sb.AppendLine($"{indent}    public global::Paradise.ECS.ReadOnlyComponentSegments<global::{comp.ComponentFullName}, TMask, TConfig> {comp.PropertyName}");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
                sb.AppendLine($"{indent}        get => new(_chunkManager, _readSegments);");
                sb.AppendLine($"{indent}    }}");
            }
            else
            {
                sb.AppendLine($"{indent}    /// <summary>Writable flat view over all {comp.ComponentTypeName} components (WRITE table).</summary>");
                sb.AppendLine($"{indent}    public global::Paradise.ECS.ComponentSegments<global::{comp.ComponentFullName}, TMask, TConfig> {comp.PropertyName}");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
                sb.AppendLine($"{indent}        get => new(_chunkManager, _writeSegments);");
                sb.AppendLine($"{indent}    }}");
            }
        }

        sb.AppendLine($"{indent}}}");
    }

    /// <summary>
    /// Generates the nested Singleton struct for <c>[Queryable(Singleton = true)]</c>: the
    /// queryable resolved ONCE against exactly one matching entity. <c>Resolve</c> runs the
    /// query against the (write) world, throws unless exactly one entity matches, pairs the
    /// entity's chunk against the read world (snapshot-read execution; pass null to bind reads
    /// to the write world — classic execution or <c>[CurrentTick]</c>), and wraps a
    /// <c>Data</c> view so component access rules match composition data exactly.
    /// </summary>
    private static void GenerateNestedSingletonStruct(StringBuilder sb, QueryableInfo queryable, string indent)
    {
        var queryableFQN = "global::" + queryable.FullyQualifiedName.Replace("+", ".");

        sb.AppendLine();
        sb.AppendLine($"{indent}/// <summary>");
        sb.AppendLine($"{indent}/// Singleton view: this queryable resolved against EXACTLY one matching entity.");
        sb.AppendLine($"{indent}/// Component access matches Data (ref / ref readonly per With IsReadOnly).");
        sb.AppendLine($"{indent}/// </summary>");
        sb.AppendLine($"{indent}/// <typeparam name=\"TMask\">The component mask type implementing IBitSet.</typeparam>");
        sb.AppendLine($"{indent}/// <typeparam name=\"TConfig\">The world configuration type.</typeparam>");
        sb.AppendLine($"{indent}public readonly ref struct Singleton<TMask, TConfig>");
        sb.AppendLine($"{indent}    where TMask : unmanaged, global::Paradise.ECS.IBitSet<TMask>");
        sb.AppendLine($"{indent}    where TConfig : global::Paradise.ECS.IConfig, new()");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    private readonly Data<TMask, TConfig> _data;");
        sb.AppendLine();
        sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}    internal Singleton(Data<TMask, TConfig> data)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        _data = data;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>Resolves the singleton by running this queryable's query against");
        sb.AppendLine($"{indent}    /// <paramref name=\"world\"/> (the write world — cardinality is checked there).");
        sb.AppendLine($"{indent}    /// Read-only components bind to the paired chunk in <paramref name=\"readWorld\"/>");
        sb.AppendLine($"{indent}    /// when provided (snapshot-read execution); pass null to bind every component to");
        sb.AppendLine($"{indent}    /// the write world (classic execution or [CurrentTick] fresh reads).</summary>");
        sb.AppendLine($"{indent}    /// <exception cref=\"global::System.InvalidOperationException\">Thrown when the query");
        sb.AppendLine($"{indent}    /// matches zero or more than one entity.</exception>");
        sb.AppendLine($"{indent}    public static Singleton<TMask, TConfig> Resolve(");
        sb.AppendLine($"{indent}        global::Paradise.ECS.IWorld<TMask, TConfig> world,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.IWorld<TMask, TConfig>? readWorld)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        var query = world.ArchetypeRegistry.GetOrCreateQuery(global::Paradise.ECS.QueryableRegistry<TMask>.Descriptions[{queryableFQN}.QueryableId]);");
        sb.AppendLine($"{indent}        int count = 0;");
        sb.AppendLine($"{indent}        global::Paradise.ECS.ChunkHandle chunk = default;");
        sb.AppendLine($"{indent}        nint layoutData = 0;");
        sb.AppendLine($"{indent}        int archetypeId = 0;");
        sb.AppendLine($"{indent}        int chunkIndex = 0;");
        if (queryable.IsFiltered)
        {
            // Tag-filtered: the match can sit at any row of any chunk, and the chunk's entity
            // count is not the match count. Both facts break the unfiltered form below, which
            // sums chunk counts and then binds index 0.
            sb.AppendLine($"{indent}        int indexInChunk = 0;");
            sb.AppendLine($"{indent}        int writeEntityCount = 1;");
            sb.AppendLine($"{indent}        foreach (var ci in query.Chunks)");
            sb.AppendLine($"{indent}        {{");
            // Same coarse pass the enumerator does. It matters most HERE: a singleton resolves once
            // per step for the lifetime of a run, so skipping chunks that cannot hold the match is
            // the difference between scanning an archetype every tick and glancing at its chunks.
            sb.AppendLine($"{indent}            if (!Data<TMask, TConfig>.ChunkMatches(world.ChunkManager, ci.Archetype.Layout, ci.Handle))");
            sb.AppendLine($"{indent}                continue;");
            sb.AppendLine($"{indent}            for (int i = 0; i < ci.EntityCount; i++)");
            sb.AppendLine($"{indent}            {{");
            sb.AppendLine($"{indent}                if (!Data<TMask, TConfig>.Matches(world.ChunkManager, ci.Archetype.Layout, ci.Handle, i))");
            sb.AppendLine($"{indent}                    continue;");
            sb.AppendLine($"{indent}                if (count == 0)");
            sb.AppendLine($"{indent}                {{");
            sb.AppendLine($"{indent}                    chunk = ci.Handle;");
            sb.AppendLine($"{indent}                    layoutData = ci.Archetype.Layout.DataPointer;");
            sb.AppendLine($"{indent}                    archetypeId = ci.Archetype.Id;");
            sb.AppendLine($"{indent}                    chunkIndex = ci.ChunkIndex;");
            sb.AppendLine($"{indent}                    indexInChunk = i;");
            sb.AppendLine($"{indent}                    writeEntityCount = ci.EntityCount;");
            sb.AppendLine($"{indent}                }}");
            sb.AppendLine($"{indent}                count++;");
            sb.AppendLine($"{indent}            }}");
            sb.AppendLine($"{indent}        }}");
        }
        else
        {
            sb.AppendLine($"{indent}        const int indexInChunk = 0;");
            sb.AppendLine($"{indent}        const int writeEntityCount = 1;");
            sb.AppendLine($"{indent}        foreach (var ci in query.Chunks)");
            sb.AppendLine($"{indent}        {{");
            sb.AppendLine($"{indent}            if (count == 0 && ci.EntityCount > 0)");
            sb.AppendLine($"{indent}            {{");
            sb.AppendLine($"{indent}                chunk = ci.Handle;");
            sb.AppendLine($"{indent}                layoutData = ci.Archetype.Layout.DataPointer;");
            sb.AppendLine($"{indent}                archetypeId = ci.Archetype.Id;");
            sb.AppendLine($"{indent}                chunkIndex = ci.ChunkIndex;");
            sb.AppendLine($"{indent}            }}");
            sb.AppendLine($"{indent}            count += ci.EntityCount;");
            sb.AppendLine($"{indent}        }}");
        }
        sb.AppendLine($"{indent}        if (count != 1)");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            throw new global::System.InvalidOperationException(");
        sb.AppendLine($"{indent}                \"Singleton queryable '{queryable.FullyQualifiedName}' must resolve to exactly one entity, but the query matched \" + count + \" entities.\");");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}        global::Paradise.ECS.SnapshotChunkPairing.Resolve(world, readWorld, archetypeId, chunkIndex, chunk, writeEntityCount, out var readChunkManager, out var readChunk);");
        sb.AppendLine($"{indent}        return new Singleton<TMask, TConfig>(new Data<TMask, TConfig>(world.ChunkManager, new global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig>(layoutData), chunk, readChunkManager, readChunk, indexInChunk));");
        sb.AppendLine($"{indent}    }}");

        // Forward component properties to the wrapped Data view (unless QueryOnly)
        foreach (var comp in queryable.WithComponentsAccess)
        {
            if (comp.QueryOnly)
                continue;

            sb.AppendLine();
            var refType = comp.IsReadOnly ? "ref readonly" : "ref";
            sb.AppendLine($"{indent}    /// <summary>Gets a {(comp.IsReadOnly ? "read-only " : "")}reference to the singleton's {comp.ComponentTypeName} component.</summary>");
            sb.AppendLine($"{indent}    public {refType} global::{comp.ComponentFullName} {comp.PropertyName}");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"{indent}        get => ref _data.{comp.PropertyName};");
            sb.AppendLine($"{indent}    }}");
        }

        // Forward Has/Get accessors for Optional<T> components
        foreach (var opt in queryable.OptionalComponents)
        {
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>Gets whether the singleton has the {opt.ComponentTypeName} component.</summary>");
            sb.AppendLine($"{indent}    public bool Has{opt.PropertyName}");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"{indent}        get => _data.Has{opt.PropertyName};");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            var refType = opt.IsReadOnly ? "ref readonly" : "ref";
            sb.AppendLine($"{indent}    /// <summary>Gets a {(opt.IsReadOnly ? "read-only " : "")}reference to the singleton's {opt.ComponentTypeName} component.</summary>");
            sb.AppendLine($"{indent}    /// <exception cref=\"global::System.InvalidOperationException\">Thrown when the component is not present. Check Has{opt.PropertyName} first.</exception>");
            sb.AppendLine($"{indent}    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"{indent}    public {refType} global::{opt.ComponentFullName} Get{opt.PropertyName}()");
            sb.AppendLine($"{indent}        => ref _data.Get{opt.PropertyName}();");
        }

        sb.AppendLine($"{indent}}}");
    }

    /// <summary>
    /// Nested non-generic views with the project's default mask/config baked in:
    /// <c>Entity</c>, <c>Chunk</c>, <c>Segments</c>, <c>ReadLookup</c>, <c>WriteLookup</c>,
    /// and <c>Singleton</c> (when opted in). Each wraps the matching generic nested type and
    /// forwards accessors, with an implicit conversion FROM that generic type so system
    /// injection (which still constructs <c>Data&lt;TMask, TConfig&gt;</c> etc.) assigns
    /// cleanly onto a <c>PlayerAvatar.Entity</c> field.
    /// </summary>
    private static void GenerateDefaultConfigViews(
        StringBuilder sb, QueryableInfo queryable, string indent,
        string maskType, string configType)
    {
        var typeName = queryable.TypeName;
        GenerateDefaultDataView(sb, queryable, indent, maskType, configType);
        GenerateDefaultChunkView(sb, queryable, indent, maskType, configType);
        GenerateDefaultSegmentsView(sb, queryable, indent, typeName, maskType, configType);
        GenerateDefaultAccessorView(sb, queryable, indent, typeName, maskType, configType, reader: true);
        GenerateDefaultAccessorView(sb, queryable, indent, typeName, maskType, configType, reader: false);
        if (queryable.IsSingleton)
            GenerateDefaultSingletonView(sb, queryable, indent, typeName, maskType, configType);
    }

    private static void GenerateDefaultDataView(
        StringBuilder sb, QueryableInfo queryable, string indent,
        string maskType, string configType)
    {
        var inner = $"Data<{maskType}, {configType}>";
        sb.AppendLine();
        sb.AppendLine($"{indent}/// <summary>Default-config entity view of {queryable.TypeName}. Same accessors as Data.");
        sb.AppendLine($"{indent}/// Declare as a field on an <c>IEntitySystem</c>: <c>public {queryable.TypeName}.Entity Avatar;</c></summary>");
        sb.AppendLine($"{indent}public readonly ref struct Entity");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    private readonly {inner} _data;");
        sb.AppendLine();
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    internal Entity({inner} data) => _data = data;");
        sb.AppendLine();
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    public static implicit operator Entity({inner} data) => new(data);");
        GenerateDataPropertyForwards(sb, queryable, indent + "    ", "_data");
        sb.AppendLine($"{indent}}}");
    }

    private static void GenerateDefaultChunkView(
        StringBuilder sb, QueryableInfo queryable, string indent,
        string maskType, string configType)
    {
        var inner = $"ChunkData<{maskType}, {configType}>";
        sb.AppendLine();
        sb.AppendLine($"{indent}/// <summary>Default-config chunk view of {queryable.TypeName}. Same accessors as ChunkData");
        sb.AppendLine($"{indent}/// (EntityCount and component spans; ChunkData has no per-row entity handle).");
        sb.AppendLine($"{indent}/// Declare as a field on an <c>IChunkSystem</c>: <c>public {queryable.TypeName}.Chunk Batch;</c></summary>");
        sb.AppendLine($"{indent}public readonly ref struct Chunk");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    private readonly {inner} _data;");
        sb.AppendLine();
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    internal Chunk({inner} data) => _data = data;");
        sb.AppendLine();
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    public static implicit operator Chunk({inner} data) => new(data);");
        sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>Gets the number of entities in this chunk.</summary>");
        sb.AppendLine($"{indent}    public int EntityCount");
        sb.AppendLine($"{indent}    {{");
        AppendAggressiveInlining(sb, indent + "        ");
        sb.AppendLine($"{indent}        get => _data.EntityCount;");
        sb.AppendLine($"{indent}    }}");

        foreach (var comp in queryable.WithComponentsAccess)
        {
            if (comp.QueryOnly) continue;
            var spanPropertyName = comp.PropertyName + "Span";
            var spanType = comp.IsReadOnly ? "ReadOnlySpan" : "Span";
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>Gets a {(comp.IsReadOnly ? "read-only " : "")}span over all {comp.ComponentTypeName} components in this chunk.</summary>");
            sb.AppendLine($"{indent}    public global::System.{spanType}<global::{comp.ComponentFullName}> {spanPropertyName}");
            sb.AppendLine($"{indent}    {{");
            AppendAggressiveInlining(sb, indent + "        ");
            sb.AppendLine($"{indent}        get => _data.{spanPropertyName};");
            sb.AppendLine($"{indent}    }}");
        }

        foreach (var opt in queryable.OptionalComponents)
        {
            var spanMethodName = "Get" + opt.PropertyName + "Span";
            var optSpanType = opt.IsReadOnly ? "ReadOnlySpan" : "Span";
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>Gets whether this chunk's archetype has the {opt.ComponentTypeName} component.</summary>");
            sb.AppendLine($"{indent}    public bool Has{opt.PropertyName}");
            sb.AppendLine($"{indent}    {{");
            AppendAggressiveInlining(sb, indent + "        ");
            sb.AppendLine($"{indent}        get => _data.Has{opt.PropertyName};");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>Gets a {(opt.IsReadOnly ? "read-only " : "")}span over all {opt.ComponentTypeName} components in this chunk.</summary>");
            sb.AppendLine($"{indent}    /// <exception cref=\"global::System.InvalidOperationException\">Thrown when the component is not present. Check Has{opt.PropertyName} first.</exception>");
            AppendAggressiveInlining(sb, indent + "    ");
            sb.AppendLine($"{indent}    public global::System.{optSpanType}<global::{opt.ComponentFullName}> {spanMethodName}()");
            sb.AppendLine($"{indent}        => _data.{spanMethodName}();");
        }

        sb.AppendLine($"{indent}}}");
    }

    private static void GenerateDefaultSegmentsView(
        StringBuilder sb, QueryableInfo queryable, string indent,
        string typeName, string maskType, string configType)
    {
        var inner = $"{typeName}.Segments<{maskType}, {configType}>";
        sb.AppendLine();
        sb.AppendLine($"{indent}/// <summary>Default-config whole-query view of {queryable.TypeName}. Same accessors as Segments&lt;TMask, TConfig&gt;.");
        sb.AppendLine($"{indent}/// Declare as a field on an <c>IWorldSystem</c>: <c>public {queryable.TypeName}.Segments Rows;</c></summary>");
        sb.AppendLine($"{indent}public readonly ref struct Segments");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    private readonly {inner} _inner;");
        sb.AppendLine();
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    internal Segments({inner} inner) => _inner = inner;");
        sb.AppendLine();
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    public static implicit operator Segments({inner} inner) => new(inner);");
        sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>Total entity count across all matching chunks.</summary>");
        sb.AppendLine($"{indent}    public int Length");
        sb.AppendLine($"{indent}    {{");
        AppendAggressiveInlining(sb, indent + "        ");
        sb.AppendLine($"{indent}        get => _inner.Length;");
        sb.AppendLine($"{indent}    }}");

        foreach (var comp in queryable.WithComponentsAccess)
        {
            if (comp.QueryOnly) continue;
            sb.AppendLine();
            if (comp.IsReadOnly)
            {
                sb.AppendLine($"{indent}    /// <summary>Read-only flat view over all {comp.ComponentTypeName} components (READ table).</summary>");
                sb.AppendLine($"{indent}    public global::Paradise.ECS.ReadOnlyComponentSegments<global::{comp.ComponentFullName}, {maskType}, {configType}> {comp.PropertyName}");
            }
            else
            {
                sb.AppendLine($"{indent}    /// <summary>Writable flat view over all {comp.ComponentTypeName} components (WRITE table).</summary>");
                sb.AppendLine($"{indent}    public global::Paradise.ECS.ComponentSegments<global::{comp.ComponentFullName}, {maskType}, {configType}> {comp.PropertyName}");
            }
            sb.AppendLine($"{indent}    {{");
            AppendAggressiveInlining(sb, indent + "        ");
            sb.AppendLine($"{indent}        get => _inner.{comp.PropertyName};");
            sb.AppendLine($"{indent}    }}");
        }

        sb.AppendLine($"{indent}}}");
    }

    private static void GenerateDefaultAccessorView(
        StringBuilder sb, QueryableInfo queryable, string indent,
        string typeName, string maskType, string configType, bool reader)
    {
        var viewName = reader ? "ReadLookup" : "WriteLookup";
        var inner = $"{typeName}.{viewName}<{maskType}, {configType}>";
        var dataType = reader
            ? $"{typeName}.ReadData<{maskType}, {configType}>"
            : $"{typeName}.Data<{maskType}, {configType}>";
        sb.AppendLine();
        sb.AppendLine($"{indent}/// <summary>Default-config {(reader ? "read" : "read/write")} handle lookup into matching {queryable.TypeName} entities.</summary>");
        sb.AppendLine($"{indent}public readonly ref struct {viewName}");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    private readonly {inner} _inner;");
        sb.AppendLine();
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    public {viewName}(global::Paradise.ECS.IWorld<{maskType}, {configType}> world)");
        sb.AppendLine($"{indent}        => _inner = new {inner}(world);");
        sb.AppendLine();
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    internal {viewName}({inner} inner) => _inner = inner;");
        sb.AppendLine();
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    public static implicit operator {viewName}({inner} inner) => new(inner);");
        sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>Returns whether the entity is alive and matches {queryable.TypeName}.</summary>");
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    public bool Has(global::Paradise.ECS.Entity entity) => _inner.Has(entity);");
        sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>Returns a live component view when the entity matches.</summary>");
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    public bool TryGet(global::Paradise.ECS.Entity entity, out {dataType} data)");
        sb.AppendLine($"{indent}        => _inner.TryGet(entity, out data);");
        sb.AppendLine($"{indent}}}");
    }

    private static void GenerateDefaultSingletonView(
        StringBuilder sb, QueryableInfo queryable, string indent,
        string typeName, string maskType, string configType)
    {
        var inner = $"{typeName}.Singleton<{maskType}, {configType}>";
        sb.AppendLine();
        sb.AppendLine($"{indent}/// <summary>Default-config singleton view of {queryable.TypeName}. Same accessors as Singleton&lt;TMask, TConfig&gt;.");
        sb.AppendLine($"{indent}/// Declare as a field on any system: <c>public {queryable.TypeName}.Singleton Frame;</c></summary>");
        sb.AppendLine($"{indent}public readonly ref struct Singleton");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    private readonly {inner} _inner;");
        sb.AppendLine();
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    internal Singleton({inner} inner) => _inner = inner;");
        sb.AppendLine();
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    public static implicit operator Singleton({inner} inner) => new(inner);");
        sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>Resolves the singleton against <paramref name=\"world\"/> by delegating to the generic Singleton type.</summary>");
        AppendAggressiveInlining(sb, indent + "    ");
        sb.AppendLine($"{indent}    public static Singleton Resolve(");
        sb.AppendLine($"{indent}        global::Paradise.ECS.IWorld<{maskType}, {configType}> world,");
        sb.AppendLine($"{indent}        global::Paradise.ECS.IWorld<{maskType}, {configType}>? readWorld)");
        sb.AppendLine($"{indent}        => new({inner}.Resolve(world, readWorld));");
        GenerateDataPropertyForwards(sb, queryable, indent + "    ", "_inner");
        sb.AppendLine($"{indent}}}");
    }

    private static void GenerateDataPropertyForwards(
        StringBuilder sb, QueryableInfo queryable, string indent, string innerField)
    {
        foreach (var comp in queryable.WithComponentsAccess)
        {
            if (comp.QueryOnly) continue;
            var refType = comp.IsReadOnly ? "ref readonly" : "ref";
            sb.AppendLine();
            sb.AppendLine($"{indent}/// <summary>Gets a {(comp.IsReadOnly ? "read-only " : "")}reference to the {comp.ComponentTypeName} component.</summary>");
            sb.AppendLine($"{indent}public {refType} global::{comp.ComponentFullName} {comp.PropertyName}");
            sb.AppendLine($"{indent}{{");
            AppendAggressiveInlining(sb, indent + "    ");
            sb.AppendLine($"{indent}    get => ref {innerField}.{comp.PropertyName};");
            sb.AppendLine($"{indent}}}");
        }

        foreach (var opt in queryable.OptionalComponents)
        {
            var refType = opt.IsReadOnly ? "ref readonly" : "ref";
            sb.AppendLine();
            sb.AppendLine($"{indent}/// <summary>Gets whether the {opt.ComponentTypeName} component is present.</summary>");
            sb.AppendLine($"{indent}public bool Has{opt.PropertyName}");
            sb.AppendLine($"{indent}{{");
            AppendAggressiveInlining(sb, indent + "    ");
            sb.AppendLine($"{indent}    get => {innerField}.Has{opt.PropertyName};");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine();
            sb.AppendLine($"{indent}/// <summary>Gets a {(opt.IsReadOnly ? "read-only " : "")}reference to the {opt.ComponentTypeName} component.</summary>");
            sb.AppendLine($"{indent}/// <exception cref=\"global::System.InvalidOperationException\">Thrown when the component is not present. Check Has{opt.PropertyName} first.</exception>");
            AppendAggressiveInlining(sb, indent);
            sb.AppendLine($"{indent}public {refType} global::{opt.ComponentFullName} Get{opt.PropertyName}()");
            sb.AppendLine($"{indent}    => ref {innerField}.Get{opt.PropertyName}();");
        }
    }

    private static void AppendAggressiveInlining(StringBuilder sb, string indent)
        => sb.AppendLine($"{indent}[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");

    private static void GenerateQueryableRegistry(
        SourceProductionContext context,
        List<(QueryableInfo Info, int TypeId)> queryables,
        int componentCount,
        bool suppressGlobalUsings)
    {
        // Find max ID
        int maxId = queryables.Max(q => q.TypeId);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Paradise.ECS;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Registry containing query descriptions and component masks for all queryable types.");
        sb.AppendLine("/// Indexed by queryable QueryableId for O(1) lookup.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("/// <typeparam name=\"TMask\">The component mask type implementing IBitSet.</typeparam>");
        sb.AppendLine("public static class QueryableRegistry<TMask>");
        sb.AppendLine("    where TMask : unmanaged, global::Paradise.ECS.IBitSet<TMask>");
        sb.AppendLine("{");
        sb.AppendLine($"    private static readonly global::System.Collections.Immutable.ImmutableArray<global::Paradise.ECS.HashedKey<global::Paradise.ECS.ImmutableQueryDescription<TMask>>> s_descriptions;");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Gets the query descriptions for all queryable types, indexed by QueryableId.");
        sb.AppendLine("    /// Descriptions are pre-wrapped in HashedKey for efficient lookup without re-computing hash.");
        sb.AppendLine("    /// Access All, None, Any masks via Description[id].Value.All/None/Any.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine($"    public static global::System.Collections.Immutable.ImmutableArray<global::Paradise.ECS.HashedKey<global::Paradise.ECS.ImmutableQueryDescription<TMask>>> Descriptions => s_descriptions;");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Gets the total number of registered queryable types.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine($"    public static int Count => {queryables.Count};");
        sb.AppendLine();
        sb.AppendLine("    static QueryableRegistry()");
        sb.AppendLine("    {");
        sb.AppendLine($"        var descriptions = new global::Paradise.ECS.HashedKey<global::Paradise.ECS.ImmutableQueryDescription<TMask>>[{maxId + 1}];");
        sb.AppendLine();

        // Generate mask initialization for each queryable
        foreach (var (info, typeId) in queryables)
        {
            sb.AppendLine($"        // {info.FullyQualifiedName} (QueryableId = {typeId})");
            sb.Append($"        var allMask{typeId} = ");
            GenerateMask(sb, info.WithComponents);
            sb.AppendLine(";");
            sb.Append($"        var noneMask{typeId} = ");
            GenerateMask(sb, info.WithoutComponents);
            sb.AppendLine(";");
            sb.Append($"        var anyMask{typeId} = ");
            GenerateMask(sb, info.AnyComponents);
            sb.AppendLine(";");
            sb.AppendLine($"        descriptions[{typeId}] = (global::Paradise.ECS.HashedKey<global::Paradise.ECS.ImmutableQueryDescription<TMask>>)new global::Paradise.ECS.ImmutableQueryDescription<TMask>(allMask{typeId}, noneMask{typeId}, anyMask{typeId});");
            sb.AppendLine();
        }

        sb.AppendLine("        s_descriptions = global::System.Collections.Immutable.ImmutableArray.Create(descriptions);");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("QueryableRegistry.g.cs", sb.ToString());

        // Generate type alias and module initializer in separate files
        GenerateQueryableAliases(context, componentCount, suppressGlobalUsings);
        GenerateModuleInitializer(context, queryables);
    }

    private static void GenerateQueryableAliases(
        SourceProductionContext context,
        int componentCount,
        bool suppressGlobalUsings)
    {
        var maskTypeFullyQualified = GeneratorUtilities.GetOptimalMaskType(componentCount);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine();

        if (suppressGlobalUsings)
        {
            sb.AppendLine("// All global usings suppressed by [assembly: SuppressGlobalUsings]");
            sb.AppendLine($"// To use QueryableRegistry, reference: global::Paradise.ECS.QueryableRegistry<{maskTypeFullyQualified}>");
        }
        else
        {
            sb.AppendLine("// Type alias for QueryableRegistry using the same mask type as components");
            sb.AppendLine($"global using QueryableRegistry = global::Paradise.ECS.QueryableRegistry<{maskTypeFullyQualified}>;");
        }

        context.AddSource("QueryableAliases.g.cs", sb.ToString());
    }

    private static void GenerateModuleInitializer(
        SourceProductionContext context,
        List<(QueryableInfo Info, int TypeId)> queryables)
    {
        // Get namespace from first queryable for module initializer placement
        var firstQueryable = queryables.FirstOrDefault();
        var ns = firstQueryable.Info.Namespace ?? "Paradise.ECS";

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Module initializer that ensures QueryableRegistry is initialized when the assembly loads.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("internal static class QueryableRegistryInitializer");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Initialize()");
        sb.AppendLine("    {");
        sb.AppendLine("        // Access Count to trigger static constructor");
        sb.AppendLine("        _ = QueryableRegistry.Count;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("QueryableRegistryInitializer.g.cs", sb.ToString());
    }

    /// <summary>
    /// Emits <c>IQueryData.IsFiltered</c> and <c>IQueryData.Matches</c> for a queryable declaring
    /// <c>[WithTag&lt;T&gt;]</c> — and nothing at all for one that does not, which is every
    /// queryable that existed before tags and the reason this is additive.
    ///
    /// The test reads the entity's <c>EntityTags</c> component straight out of the chunk and checks
    /// one bit per required tag. It reads the WRITE chunk deliberately: which entities a query
    /// matches is a question about the world being iterated, not about the previous tick's
    /// snapshot, and the generated singleton counts cardinality there for the same reason.
    /// </summary>
    private static void GenerateRowFilter(
        StringBuilder sb, QueryableInfo queryable, string indent, string rootNamespace)
    {
        if (!queryable.IsFiltered)
            return;

        var tagList = string.Join(", ", queryable.WithTags.Select(static t => t.Split('.').Last()));

        sb.AppendLine();
        sb.AppendLine($"{indent}/// <summary>This queryable filters rows by tag ({tagList}), so counting");
        sb.AppendLine($"{indent}/// cannot be answered from archetype bookkeeping alone.</summary>");
        sb.AppendLine($"{indent}public static bool IsFiltered => true;");
        sb.AppendLine();
        sb.AppendLine($"{indent}/// <summary>Whether this row carries every required tag ({tagList}).</summary>");
        sb.AppendLine($"{indent}[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}public static bool Matches(");
        sb.AppendLine($"{indent}    global::Paradise.ECS.ChunkManager chunkManager,");
        sb.AppendLine($"{indent}    global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig> layout,");
        sb.AppendLine($"{indent}    global::Paradise.ECS.ChunkHandle chunk,");
        sb.AppendLine($"{indent}    int indexInChunk)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    ref readonly var __tags = ref chunkManager.GetBytes(chunk).GetRef<global::{rootNamespace}.EntityTags>(");
        sb.AppendLine($"{indent}        layout.GetBaseOffset(global::{rootNamespace}.EntityTags.TypeId) + indexInChunk * global::{rootNamespace}.EntityTags.Size);");
        sb.Append($"{indent}    return ");
        var first = true;
        foreach (var tag in queryable.WithTags)
        {
            if (!first) sb.Append($"\n{indent}        && ");
            sb.Append($"__tags.Mask.Get(global::{tag}.TagId)");
            first = false;
        }
        sb.AppendLine(";");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();

        // The COARSE pass. The chunk carries the union of its entities' tags in a slot the layout
        // reserved for EntityTags, so a chunk missing any required bit provably holds no match and
        // is skipped without reading a single row.
        sb.AppendLine($"{indent}/// <summary>Whether this chunk can hold a row carrying every required tag ({tagList}).");
        sb.AppendLine($"{indent}/// Conservative: the chunk mask keeps bits after a tag is removed, so true still has to be");
        sb.AppendLine($"{indent}/// confirmed row by row; only false is proof.</summary>");
        sb.AppendLine($"{indent}[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}public static bool ChunkMatches(");
        sb.AppendLine($"{indent}    global::Paradise.ECS.ChunkManager chunkManager,");
        sb.AppendLine($"{indent}    global::Paradise.ECS.ImmutableArchetypeLayout<TMask, TConfig> layout,");
        sb.AppendLine($"{indent}    global::Paradise.ECS.ChunkHandle chunk)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    int __offset = layout.GetChunkAggregateOffset(");
        sb.AppendLine($"{indent}        global::{rootNamespace}.EntityTags.TypeId, global::{rootNamespace}.ComponentRegistry.TypeInfosStatic);");
        sb.AppendLine($"{indent}    // No reserved slot means no summary to consult; let the rows decide.");
        sb.AppendLine($"{indent}    if (__offset < 0) return true;");
        sb.AppendLine($"{indent}    ref readonly var __chunkTags = ref chunkManager.GetBytes(chunk).GetRef<global::{rootNamespace}.EntityTags>(__offset);");
        sb.Append($"{indent}    return ");
        first = true;
        foreach (var tag in queryable.WithTags)
        {
            if (!first) sb.Append($"\n{indent}        && ");
            sb.Append($"__chunkTags.Mask.Get(global::{tag}.TagId)");
            first = false;
        }
        sb.AppendLine(";");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
    }

    /// <summary>
    /// Emits <c>Paradise.ECS.IComponentSet</c>'s CollectComponentTypes: the queryable's
    /// REQUIRED components, ORed into the caller's mask so several sets union cleanly.
    ///
    /// Only [With] contributes. [Without] would make the entity unmatchable by this very
    /// queryable, and [WithAny]/[Optional] name no single required component — including either
    /// would be a guess about what the author meant, so they are left out and documented.
    /// </summary>
    private static void GenerateCollectComponentTypes(
        StringBuilder sb, QueryableInfo queryable, string indent, string rootNamespace)
    {
        sb.AppendLine($"{indent}/// <summary>Adds this queryable's required ([With]) component types to <paramref name=\"mask\"/>.</summary>");
        sb.AppendLine($"{indent}/// <typeparam name=\"TMask\">The component mask type implementing IBitSet.</typeparam>");
        sb.AppendLine($"{indent}/// <param name=\"mask\">The mask to add component types to.</param>");
        sb.AppendLine($"{indent}[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{indent}public static void CollectComponentTypes<TMask>(ref TMask mask)");
        sb.AppendLine($"{indent}    where TMask : unmanaged, global::Paradise.ECS.IBitSet<TMask>");
        sb.AppendLine($"{indent}{{");
        if (queryable.WithComponents.IsEmpty && !queryable.IsFiltered)
        {
            sb.AppendLine($"{indent}    // No [With] components — contributes nothing to the archetype.");
        }
        else
        {
            sb.Append($"{indent}    mask = mask");
            foreach (var component in queryable.WithComponents)
            {
                sb.Append($".Set(global::{component}.TypeId)");
            }
            if (queryable.IsFiltered)
            {
                // [WithTag<T>] contributes EntityTags — the component the tag bits live in. It is
                // what lets archetype matching reject anything that cannot carry a tag at all
                // before a single row is read, and it is also what the row test reads.
                sb.Append($".Set(global::{rootNamespace}.EntityTags.TypeId)");
            }
            sb.AppendLine(";");
        }
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
    }

    private static void GenerateMask(StringBuilder sb, ImmutableArray<string> components)
    {
        if (components.IsEmpty)
        {
            sb.Append("TMask.Empty");
            return;
        }

        // Generate mask by ORing component TypeIds
        sb.Append("TMask.Empty");
        foreach (var component in components)
        {
            sb.Append($".Set(global::{component}.TypeId)");
        }
    }

    private readonly struct QueryableInfo
    {
        public string FullyQualifiedName { get; }
        public Location Location { get; }
        public bool IsRefStruct { get; }
        public bool IsPartial { get; }
        public string? Namespace { get; }
        public string TypeName { get; }
        public ImmutableArray<ContainingTypeInfo> ContainingTypes { get; }
        public int? ManualId { get; }
        public bool IsSingleton { get; }
        public ImmutableArray<string> WithComponents { get; }
        public ImmutableArray<ComponentInfo> WithComponentsAccess { get; }
        public ImmutableArray<string> WithoutComponents { get; }
        public ImmutableArray<string> AnyComponents { get; }
        public ImmutableArray<ComponentInfo> OptionalComponents { get; }

        /// <summary>Tag types from <c>[WithTag&lt;T&gt;]</c>. Not components: they constrain rows
        /// rather than archetypes, so they never enter the query description's masks — what they
        /// add is the EntityTags requirement and a per-row test.</summary>
        public ImmutableArray<string> WithTags { get; }

        public ImmutableArray<(string Component, List<string> Attributes)> DuplicateComponents { get; }

        public bool HasDuplicates => !DuplicateComponents.IsEmpty;

        /// <summary>Whether iteration has to test each row — see
        /// <c>Paradise.ECS.IQueryData.IsFiltered</c>.</summary>
        public bool IsFiltered => !WithTags.IsEmpty;

        /// <summary>
        /// Gets the unique helper struct name prefix that includes containing type names.
        /// For nested types like A.B.Player, returns "ABPlayer".
        /// For non-nested types like Player, returns "Player".
        /// </summary>
        public string HelperStructPrefix
        {
            get
            {
                if (ContainingTypes.IsEmpty)
                    return TypeName;

                var sb = new StringBuilder();
                foreach (var containingType in ContainingTypes)
                {
                    sb.Append(containingType.Name);
                }
                sb.Append(TypeName);
                return sb.ToString();
            }
        }

        public QueryableInfo(
            string fullyQualifiedName,
            Location location,
            bool isRefStruct,
            bool isPartial,
            string? ns,
            string typeName,
            ImmutableArray<ContainingTypeInfo> containingTypes,
            int? manualId,
            bool isSingleton,
            ImmutableArray<string> withComponents,
            ImmutableArray<ComponentInfo> withComponentsAccess,
            ImmutableArray<string> withoutComponents,
            ImmutableArray<string> anyComponents,
            ImmutableArray<ComponentInfo> optionalComponents,
            ImmutableArray<string> withTags,
            ImmutableArray<(string Component, List<string> Attributes)> duplicateComponents)
        {
            FullyQualifiedName = fullyQualifiedName;
            Location = location;
            IsRefStruct = isRefStruct;
            IsPartial = isPartial;
            Namespace = ns;
            TypeName = typeName;
            ContainingTypes = containingTypes;
            ManualId = manualId;
            IsSingleton = isSingleton;
            WithComponents = withComponents;
            WithComponentsAccess = withComponentsAccess;
            WithoutComponents = withoutComponents;
            AnyComponents = anyComponents;
            OptionalComponents = optionalComponents;
            WithTags = withTags;
            DuplicateComponents = duplicateComponents;
        }
    }

    /// <summary>
    /// Information about a component access from With&lt;T&gt; or Optional&lt;T&gt; attribute.
    /// </summary>
    private readonly struct ComponentInfo
    {
        /// <summary>Fully qualified component type name.</summary>
        public string ComponentFullName { get; }

        /// <summary>Simple type name (without namespace).</summary>
        public string ComponentTypeName { get; }

        /// <summary>Property name (Name ?? ComponentTypeName).</summary>
        public string PropertyName { get; }

        /// <summary>If true, generates ref readonly property/method.</summary>
        public bool IsReadOnly { get; }

        /// <summary>If true, component used only for filtering, no property generated. Only applicable to With components.</summary>
        public bool QueryOnly { get; }

        public ComponentInfo(
            string componentFullName,
            string componentTypeName,
            string? customName,
            bool isReadOnly,
            bool queryOnly = false)
        {
            ComponentFullName = componentFullName;
            ComponentTypeName = componentTypeName;
            PropertyName = customName ?? componentTypeName;
            IsReadOnly = isReadOnly;
            QueryOnly = queryOnly;
        }
    }
}
