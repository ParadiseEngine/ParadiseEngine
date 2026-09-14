using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Paradise.ECS.Generators;

/// <summary>Enforces an assembly's opt-in restriction on managed component access in ECS systems.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ForbiddenManagedComponentAccessAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.ManagedComponentAccessForbiddenInSystem);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var attribute = start.Compilation.GetTypeByMetadataName("Paradise.ECS.ForbidManagedComponentsInSystemsAttribute");
            if (attribute is null || !start.Compilation.Assembly.GetAttributes().Any(a =>
                    SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute)))
                return;
            var symbols = new Symbols(start.Compilation);
            start.RegisterSyntaxNodeAction(context => AnalyzeType(context, symbols),
                SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration, SyntaxKind.RecordDeclaration,
                SyntaxKind.RecordStructDeclaration, SyntaxKind.InterfaceDeclaration);
        });
    }

    private static void AnalyzeType(SyntaxNodeAnalysisContext context, Symbols symbols)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } type ||
            symbols.FindSystem(type) is not { } system)
            return;

        var reported = new HashSet<TextSpan>();
        foreach (var name in declaration.DescendantNodes(node => node == declaration || node is not TypeDeclarationSyntax).OfType<NameSyntax>())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            // Qualified names and generic arguments are accounted for by their enclosing name.
            if (name.Parent is NameSyntax or TypeArgumentListSyntax)
                continue;
            var symbol = context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol;
            var managed = symbols.FindManaged(symbol) ??
                symbols.FindManaged(context.SemanticModel.GetTypeInfo(name, context.CancellationToken).Type);
            if (managed is null)
                continue;
            var boundary = name.Ancestors().FirstOrDefault(static node => node is
                StatementSyntax or AttributeSyntax or ParameterSyntax or MemberDeclarationSyntax) ?? name;
            if (reported.Add(boundary.Span))
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.ManagedComponentAccessForbiddenInSystem,
                    name.GetLocation(), managed.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), system.Name));
        }
    }

    private sealed class Symbols
    {
        private readonly INamedTypeSymbol? _managedComponent;
        private readonly INamedTypeSymbol? _managedAttribute;
        private readonly INamedTypeSymbol? _queryableAttribute;
        private readonly ImmutableArray<INamedTypeSymbol?> _managedFilters;
        private readonly ImmutableArray<INamedTypeSymbol?> _systems;

        public Symbols(Compilation compilation)
        {
            _managedComponent = compilation.GetTypeByMetadataName("Paradise.ECS.IManagedComponent");
            _managedAttribute = compilation.GetTypeByMetadataName("Paradise.ECS.ManagedComponentAttribute");
            _queryableAttribute = compilation.GetTypeByMetadataName("Paradise.ECS.QueryableAttribute");
            _managedFilters = ImmutableArray.Create(
                compilation.GetTypeByMetadataName("Paradise.ECS.WithManagedAttribute`1"),
                compilation.GetTypeByMetadataName("Paradise.ECS.WithoutManagedAttribute`1"),
                compilation.GetTypeByMetadataName("Paradise.ECS.WithManagedAnyAttribute`1"));
            _systems = ImmutableArray.Create(
                compilation.GetTypeByMetadataName("Paradise.ECS.IEntitySystem"),
                compilation.GetTypeByMetadataName("Paradise.ECS.IChunkSystem"),
                compilation.GetTypeByMetadataName("Paradise.ECS.IWorldSystem"));
        }

        public INamedTypeSymbol? FindSystem(INamedTypeSymbol? type)
        {
            for (; type is not null; type = type.ContainingType)
                if (type.AllInterfaces.Any(i => _systems.Any(system => SymbolEqualityComparer.Default.Equals(i, system))))
                    return type;
            return null;
        }

        public ITypeSymbol? FindManaged(ISymbol? symbol) => symbol switch
        {
            ITypeSymbol type => FindManaged(type),
            IAliasSymbol alias => FindManaged(alias.Target),
            IFieldSymbol field => FindManaged(field.Type) ?? FindManaged(field.ContainingType),
            IPropertySymbol property => FindManaged(property.Type) ?? FindManaged(property.ContainingType),
            IEventSymbol @event => FindManaged(@event.Type) ?? FindManaged(@event.ContainingType),
            IParameterSymbol parameter => FindManaged(parameter.Type),
            ILocalSymbol local => FindManaged(local.Type),
            IMethodSymbol method => FindManaged(method.ReturnType) ?? FindManaged(method.ContainingType) ??
                method.TypeArguments.Select(FindManaged).FirstOrDefault(static type => type is not null) ??
                method.Parameters.Select(parameter => FindManaged(parameter.Type)).FirstOrDefault(static type => type is not null),
            _ => null
        };

        public ITypeSymbol? FindManaged(ITypeSymbol? type)
            => FindManaged(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

        private ITypeSymbol? FindManaged(ITypeSymbol? type, HashSet<ITypeSymbol> visited)
        {
            if (type is null || !visited.Add(type))
                return null;
            if (SymbolEqualityComparer.Default.Equals(type, _managedComponent))
                return type;
            if (type is ITypeParameterSymbol parameter)
                return parameter.ConstraintTypes.Select(constraint => FindManaged(constraint, visited))
                    .FirstOrDefault(static managed => managed is not null);
            if (type is IArrayTypeSymbol array)
                return FindManaged(array.ElementType, visited);
            if (type is IPointerTypeSymbol pointer)
                return FindManaged(pointer.PointedAtType, visited);
            if (type is not INamedTypeSymbol named)
                return null;
            if (named.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, _managedComponent)) ||
                named.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _managedAttribute)))
                return named;
            foreach (var argument in named.TypeArguments)
                if (FindManaged(argument, visited) is { } managed)
                    return managed;
            if (FindManaged(named.ContainingType, visited) is { } containingManaged)
                return containingManaged;
            for (var query = named; query is not null; query = query.ContainingType)
            {
                var attributes = query.GetAttributes();
                if (!attributes.Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _queryableAttribute)))
                    continue;
                foreach (var attribute in attributes)
                    if (attribute.AttributeClass is { } filter && _managedFilters.Any(expected =>
                            SymbolEqualityComparer.Default.Equals(filter.OriginalDefinition, expected)))
                        foreach (var argument in filter.TypeArguments)
                            if (FindManaged(argument, visited) is { } managed)
                                return managed;
            }
            return null;
        }
    }
}
