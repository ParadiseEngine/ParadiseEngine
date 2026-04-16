using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Paradise.BT.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DuplicateGuidAnalyzer : DiagnosticAnalyzer
{
    private const string INodeDataFullName = "Paradise.BT.INodeData";
    private const string GuidAttributeFullName = "System.Runtime.InteropServices.GuidAttribute";

    internal static readonly DiagnosticDescriptor s_duplicateGuidDiagnostic = new(
        id: "PBT0003",
        title: "Duplicate Guid on INodeData struct",
        messageFormat: "Duplicate Guid \"{0}\" on INodeData struct '{1}' — also used by '{2}'. Each node type must have a unique Guid.",
        category: "Paradise.BT.Design",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd }
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(s_duplicateGuidDiagnostic);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilationContext =>
        {
            var nodeDataInterface = compilationContext.Compilation.GetTypeByMetadataName(INodeDataFullName);
            if (nodeDataInterface is null)
                return;

            var guidsByValue = new ConcurrentDictionary<string, ConcurrentBag<(INamedTypeSymbol Symbol, Location Location, string GuidValue)>>(
                StringComparer.OrdinalIgnoreCase
            );

            compilationContext.RegisterSymbolAction(symbolContext =>
            {
                var namedType = (INamedTypeSymbol)symbolContext.Symbol;

                if (namedType.TypeKind != TypeKind.Struct)
                    return;

                if (!ImplementsInterface(namedType, nodeDataInterface))
                    return;

                string? guidValue = null;
                foreach (var attr in namedType.GetAttributes())
                {
                    if (attr.AttributeClass?.ToDisplayString() == GuidAttributeFullName
                        && attr.ConstructorArguments.Length == 1
                        && attr.ConstructorArguments[0].Value is string g)
                    {
                        guidValue = g;
                        break;
                    }
                }

                if (guidValue is null)
                    return;

                var location = namedType.Locations.Length > 0
                    ? namedType.Locations[0]
                    : Location.None;

                var bag = guidsByValue.GetOrAdd(guidValue, _ => new ConcurrentBag<(INamedTypeSymbol, Location, string)>());
                bag.Add((namedType, location, guidValue));
            }, SymbolKind.NamedType);

            compilationContext.RegisterCompilationEndAction(endContext =>
            {
                foreach (var kvp in guidsByValue)
                {
                    var entries = kvp.Value.ToArray();
                    if (entries.Length < 2)
                        continue;

                    for (int i = 0; i < entries.Length; i++)
                    {
                        // Build list of other type names
                        var otherNames = new System.Collections.Generic.List<string>();
                        for (int j = 0; j < entries.Length; j++)
                        {
                            if (j != i)
                                otherNames.Add(entries[j].Symbol.ToDisplayString());
                        }
                        string otherNamesStr = string.Join(", ", otherNames);

                        endContext.ReportDiagnostic(Diagnostic.Create(
                            s_duplicateGuidDiagnostic,
                            entries[i].Location,
                            entries[i].GuidValue,
                            entries[i].Symbol.ToDisplayString(),
                            otherNamesStr
                        ));
                    }
                }
            });
        });
    }

    private static bool ImplementsInterface(INamedTypeSymbol type, INamedTypeSymbol interfaceSymbol)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, interfaceSymbol))
                return true;
        }
        return false;
    }
}
