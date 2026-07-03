using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Paradise.ECS.Generators;

/// <summary>
/// Enforces the <c>[SingleWriter]</c> contract (PECS3008): a single-writer component may have
/// WRITE access from at most one system per compilation. A component is single-writer when it
/// carries <c>[SingleWriter]</c> itself, or when its declaring ASSEMBLY carries
/// <c>[assembly: SingleWriter]</c> (which covers every <c>[Component]</c> in that assembly).
/// Write access = a non-readonly <c>ref T</c> field (IEntitySystem inline mode), a
/// <c>Span&lt;T&gt;</c> field (IChunkSystem inline mode), or a queryable composition field
/// (Data/ChunkData/Segments — every non-read-only <c>With&lt;T&gt;</c> of the queryable counts
/// as a write). Read access (<c>ref readonly T</c>, <c>ReadOnlySpan&lt;T&gt;</c>,
/// <c>IsReadOnly = true</c>) is unrestricted. Writes from plain managed code are outside the
/// system-injection model and are not tracked.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SingleWriterAnalyzer : DiagnosticAnalyzer
{
    private const string SingleWriterAttributeFullName = "Paradise.ECS.SingleWriterAttribute";
    private const string ComponentAttributeFullName = "Paradise.ECS.ComponentAttribute";
    private const string EntitySystemFullName = "Paradise.ECS.IEntitySystem";
    private const string ChunkSystemFullName = "Paradise.ECS.IChunkSystem";
    private const string WorldSystemFullName = "Paradise.ECS.IWorldSystem";
    private const string QueryableAttributeFullName = "Paradise.ECS.QueryableAttribute";
    private const string SpanMetadataName = "System.Span`1";
    private const string EcsNamespace = "Paradise.ECS";
    private const string WithAttributeName = "WithAttribute";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.SingleWriterComponentHasMultipleWriters);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static startContext =>
        {
            INamedTypeSymbol? singleWriterAttribute = startContext.Compilation.GetTypeByMetadataName(SingleWriterAttributeFullName);
            INamedTypeSymbol? componentAttribute = startContext.Compilation.GetTypeByMetadataName(ComponentAttributeFullName);
            INamedTypeSymbol? entitySystem = startContext.Compilation.GetTypeByMetadataName(EntitySystemFullName);
            INamedTypeSymbol? chunkSystem = startContext.Compilation.GetTypeByMetadataName(ChunkSystemFullName);
            INamedTypeSymbol? worldSystem = startContext.Compilation.GetTypeByMetadataName(WorldSystemFullName);
            INamedTypeSymbol? queryableAttribute = startContext.Compilation.GetTypeByMetadataName(QueryableAttributeFullName);
            INamedTypeSymbol? spanType = startContext.Compilation.GetTypeByMetadataName(SpanMetadataName);
            if (singleWriterAttribute is null || (entitySystem is null && chunkSystem is null))
            {
                return; // Paradise.ECS not referenced — nothing to enforce
            }

            // component → every system field that takes write access to it, across the compilation.
            var writersByComponent = new ConcurrentDictionary<INamedTypeSymbol, ConcurrentQueue<IFieldSymbol>>(SymbolEqualityComparer.Default);
            // assembly → whether it carries [assembly: SingleWriter] (covers all its [Component]s).
            var assemblyWide = new ConcurrentDictionary<IAssemblySymbol, bool>(SymbolEqualityComparer.Default);

            startContext.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                if (type.TypeKind != Microsoft.CodeAnalysis.TypeKind.Struct) return;
                bool isSystem = type.AllInterfaces.Any(i =>
                    SymbolEqualityComparer.Default.Equals(i, entitySystem) ||
                    SymbolEqualityComparer.Default.Equals(i, chunkSystem) ||
                    SymbolEqualityComparer.Default.Equals(i, worldSystem));
                if (!isSystem) return;

                foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>())
                {
                    if (field.IsStatic || field.IsImplicitlyDeclared) continue;

                    INamedTypeSymbol? component = GetWrittenComponent(field, spanType);
                    if (component is not null)
                    {
                        if (!IsSingleWriterComponent(component, singleWriterAttribute, componentAttribute, assemblyWide)) continue;
                        writersByComponent.GetOrAdd(component, static _ => new ConcurrentQueue<IFieldSymbol>()).Enqueue(field);
                        continue;
                    }

                    // Queryable composition field (Data/ChunkData/Segments nested in a
                    // [Queryable] type): every writable With<T> of the queryable is a write.
                    foreach (INamedTypeSymbol written in GetQueryableWrittenComponents(field, queryableAttribute))
                    {
                        if (!IsSingleWriterComponent(written, singleWriterAttribute, componentAttribute, assemblyWide)) continue;
                        writersByComponent.GetOrAdd(written, static _ => new ConcurrentQueue<IFieldSymbol>()).Enqueue(field);
                    }
                }
            }, SymbolKind.NamedType);

            startContext.RegisterCompilationEndAction(endContext =>
            {
                foreach (var pair in writersByComponent)
                {
                    IFieldSymbol[] fields = [.. pair.Value];
                    string[] systems = fields
                        .Select(field => field.ContainingType.Name)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray();
                    if (systems.Length < 2) continue;

                    string systemList = string.Join(", ", systems.Select(name => $"'{name}'"));
                    foreach (IFieldSymbol field in fields.OrderBy(f => f.ContainingType.Name, StringComparer.Ordinal))
                    {
                        endContext.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticDescriptors.SingleWriterComponentHasMultipleWriters,
                            field.Locations.FirstOrDefault(),
                            pair.Key.Name,
                            systemList));
                    }
                }
            });
        });
    }

    /// <summary>The component this field writes, or null when the field is not a write
    /// (read-only access, non-component type, or an unrelated injection kind).</summary>
    private static INamedTypeSymbol? GetWrittenComponent(IFieldSymbol field, INamedTypeSymbol? spanType)
    {
        if (field.RefKind == RefKind.Ref)
        {
            return field.Type as INamedTypeSymbol; // `ref T` — writable; `ref readonly` has RefKind.RefReadOnly
        }

        if (field.RefKind == RefKind.None
            && spanType is not null
            && field.Type is INamedTypeSymbol { IsGenericType: true } named
            && SymbolEqualityComparer.Default.Equals(named.ConstructedFrom, spanType))
        {
            return named.TypeArguments[0] as INamedTypeSymbol; // `Span<T>` — writable; ReadOnlySpan<T> is a different type
        }

        return null;
    }

    /// <summary>The writable components a queryable composition field injects: when the field's
    /// type (or its containing type, for the generated nested Data/ChunkData/Segments structs)
    /// carries [Queryable], every <c>With&lt;T&gt;</c> that is not IsReadOnly/QueryOnly is a
    /// write. Empty for non-queryable field types.</summary>
    private static System.Collections.Generic.IEnumerable<INamedTypeSymbol> GetQueryableWrittenComponents(
        IFieldSymbol field, INamedTypeSymbol? queryableAttribute)
    {
        if (queryableAttribute is null || field.RefKind != RefKind.None) yield break;
        if (field.Type is not INamedTypeSymbol fieldType) yield break;

        INamedTypeSymbol? queryable = null;
        if (HasAttribute(fieldType.GetAttributes(), queryableAttribute)) queryable = fieldType;
        else if (fieldType.ContainingType is { } outer && HasAttribute(outer.GetAttributes(), queryableAttribute)) queryable = outer;
        if (queryable is null) yield break;

        foreach (AttributeData attribute in queryable.GetAttributes())
        {
            if (attribute.AttributeClass is not { IsGenericType: true, Name: WithAttributeName } withAttribute) continue;
            if (withAttribute.ContainingNamespace.ToDisplayString() != EcsNamespace) continue;
            bool skip = attribute.NamedArguments.Any(arg =>
                (arg.Key == "IsReadOnly" || arg.Key == "QueryOnly") && arg.Value.Value is true);
            if (skip) continue;
            if (withAttribute.TypeArguments[0] is INamedTypeSymbol componentType) yield return componentType;
        }
    }

    /// <summary>A component is single-writer when it carries [SingleWriter] itself, or when its
    /// declaring assembly carries [assembly: SingleWriter] and the type is a [Component].</summary>
    private static bool IsSingleWriterComponent(
        INamedTypeSymbol component,
        INamedTypeSymbol singleWriterAttribute,
        INamedTypeSymbol? componentAttribute,
        ConcurrentDictionary<IAssemblySymbol, bool> assemblyWide)
    {
        if (HasAttribute(component.GetAttributes(), singleWriterAttribute)) return true;
        if (componentAttribute is null || !HasAttribute(component.GetAttributes(), componentAttribute)) return false;

        IAssemblySymbol assembly = component.ContainingAssembly;
        return assemblyWide.GetOrAdd(assembly, a => HasAttribute(a.GetAttributes(), singleWriterAttribute));
    }

    private static bool HasAttribute(ImmutableArray<AttributeData> attributes, INamedTypeSymbol attribute) =>
        attributes.Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));
}
