using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Paradise.ECS.Generators;

/// <summary>Warns when a system mutates an object borrowed from a managed component.</summary>
/// <remarks>
/// Tracks intraprocedural local aliases, control-flow captures and nested member access, including
/// references carried inside copied structs. It recognizes ECS readers and standard collection
/// mutators, not arbitrary method effects. Heap-stored aliases, aliases captured by local functions
/// or lambdas, alias transport across exception handlers, reflection and user-defined mutation
/// methods are outside this analysis. Handler bodies still recognize their own managed reads.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ManagedComponentMutationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.ManagedComponentMutationInSystem);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var symbols = new Symbols(start.Compilation);
            if (symbols.ManagedComponent is null && symbols.ManagedAttribute is null)
                return;
            start.RegisterOperationBlockAction(block =>
            {
                if (block.OwningSymbol is not IMethodSymbol method ||
                    method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction ||
                    !symbols.IsSystem(method.ContainingType))
                    return;
                foreach (var operation in block.OperationBlocks)
                {
                    var root = operation;
                    while (root.Parent is { } parent)
                        root = parent;
                    var graph = root switch
                    {
                        IMethodBodyOperation body => ControlFlowGraph.Create(body, block.CancellationToken),
                        IConstructorBodyOperation body => ControlFlowGraph.Create(body, block.CancellationToken),
                        IBlockOperation body => ControlFlowGraph.Create(body, block.CancellationToken),
                        _ => null
                    };
                    if (graph is not null)
                        new MethodAnalysis(symbols, method.ContainingType, block).Analyze(graph);
                }
            });
        });
    }

    private sealed class Symbols
    {
        public INamedTypeSymbol? ManagedComponent { get; }
        public INamedTypeSymbol? ManagedAttribute { get; }
        private readonly ImmutableArray<INamedTypeSymbol?> _systems;

        public Symbols(Compilation compilation)
        {
            ManagedComponent = compilation.GetTypeByMetadataName("Paradise.ECS.IManagedComponent");
            ManagedAttribute = compilation.GetTypeByMetadataName("Paradise.ECS.ManagedComponentAttribute");
            _systems = ImmutableArray.Create(
                compilation.GetTypeByMetadataName("Paradise.ECS.IEntitySystem"),
                compilation.GetTypeByMetadataName("Paradise.ECS.IChunkSystem"),
                compilation.GetTypeByMetadataName("Paradise.ECS.IWorldSystem"));
        }

        public bool IsSystem(INamedTypeSymbol type) => type.AllInterfaces.Any(i =>
            _systems.Any(system => SymbolEqualityComparer.Default.Equals(i, system)));

        public Origin ManagedOrigin(ITypeSymbol? type)
        {
            if (type is not INamedTypeSymbol { TypeKind: Microsoft.CodeAnalysis.TypeKind.Class } named)
                return default;
            return (ManagedComponent is not null && named.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, ManagedComponent))) ||
                (ManagedAttribute is not null && named.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, ManagedAttribute)))
                ? Origin.Borrowed(named) : default;
        }
    }

    private readonly struct Origin
    {
        private static readonly ImmutableHashSet<INamedTypeSymbol> s_emptyRoots =
            ImmutableHashSet<INamedTypeSymbol>.Empty.WithComparer(SymbolEqualityComparer.Default);

        private readonly ImmutableHashSet<INamedTypeSymbol>? _roots;
        public ImmutableHashSet<INamedTypeSymbol> Roots => _roots ?? s_emptyRoots;
        public bool IsBorrowed => _roots is { Count: > 0 };
        public bool IsFresh { get; }

        private Origin(ImmutableHashSet<INamedTypeSymbol>? roots, bool fresh)
        {
            _roots = roots;
            IsFresh = fresh;
        }

        public static Origin Fresh => new(null, true);
        public static Origin Borrowed(INamedTypeSymbol type) => new(s_emptyRoots.Add(type), false);
        public Origin Join(Origin other) => new(Roots.Union(other.Roots), IsFresh && other.IsFresh);
        public bool SameAs(Origin other) => IsFresh == other.IsFresh && Roots.SetEquals(other.Roots);
    }

    private sealed class State(Symbols symbols)
    {
        private readonly Dictionary<ISymbol, Origin> _symbols = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ILocalSymbol, Origin> _refLocations = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<CaptureId, Origin> _captures = new();
        private readonly Dictionary<CaptureId, Origin> _locations = new();

        public Origin Get(ISymbol symbol)
        {
            if (_symbols.TryGetValue(symbol, out var origin))
                return origin;
            return symbol switch
            {
                IParameterSymbol parameter => symbols.ManagedOrigin(parameter.Type),
                IFieldSymbol field => symbols.ManagedOrigin(field.Type),
                _ => default
            };
        }

        public void Set(ISymbol symbol, Origin origin) => _symbols[symbol] = origin;
        public Origin RefLocation(ILocalSymbol local) => _refLocations.TryGetValue(local, out var origin) ? origin : default;
        public void SetRefLocation(ILocalSymbol local, Origin origin) => _refLocations[local] = origin;
        public Origin Capture(CaptureId id) => _captures.TryGetValue(id, out var origin) ? origin : default;
        public Origin Location(CaptureId id) => _locations.TryGetValue(id, out var origin) ? origin : default;
        public void SetCapture(CaptureId id, Origin origin, Origin location)
        {
            _captures[id] = origin;
            _locations[id] = location;
        }

        public State Clone()
        {
            var copy = new State(symbols);
            foreach (var pair in _symbols) copy._symbols.Add(pair.Key, pair.Value);
            foreach (var pair in _refLocations) copy._refLocations.Add(pair.Key, pair.Value);
            foreach (var pair in _captures) copy._captures.Add(pair.Key, pair.Value);
            foreach (var pair in _locations) copy._locations.Add(pair.Key, pair.Value);
            return copy;
        }

        public bool Join(State other)
        {
            bool changed = false;
            foreach (var symbol in _symbols.Keys.Concat(other._symbols.Keys).Distinct(SymbolEqualityComparer.Default).ToArray())
            {
                var original = Get(symbol);
                var joined = original.Join(other.Get(symbol));
                if (!original.SameAs(joined))
                {
                    _symbols[symbol] = joined;
                    changed = true;
                }
            }
            foreach (var local in _refLocations.Keys.Concat(other._refLocations.Keys).Distinct<ILocalSymbol>(SymbolEqualityComparer.Default).ToArray())
            {
                var original = RefLocation(local);
                var joined = original.Join(other.RefLocation(local));
                if (!original.SameAs(joined))
                {
                    _refLocations[local] = joined;
                    changed = true;
                }
            }
            changed |= JoinCaptures(_captures, other._captures);
            changed |= JoinCaptures(_locations, other._locations);
            return changed;
        }

        private static bool JoinCaptures(Dictionary<CaptureId, Origin> target, Dictionary<CaptureId, Origin> source)
        {
            bool changed = false;
            foreach (var id in target.Keys.Concat(source.Keys).Distinct().ToArray())
            {
                target.TryGetValue(id, out var original);
                source.TryGetValue(id, out var incoming);
                var joined = original.Join(incoming);
                if (!original.SameAs(joined))
                {
                    target[id] = joined;
                    changed = true;
                }
            }
            return changed;
        }
    }

    private sealed class MethodAnalysis(Symbols symbols, INamedTypeSymbol system, OperationBlockAnalysisContext context)
    {
        private readonly HashSet<(int Start, int Length)> _reported = new();

        public void Analyze(ControlFlowGraph graph)
        {
            var incoming = new State?[graph.Blocks.Length];
            var pending = new Queue<BasicBlock>();
            var queued = new HashSet<int>();
            foreach (var block in graph.Blocks)
            {
                if (block.Ordinal != 0 && !IsHandlerEntry(block))
                    continue;
                incoming[block.Ordinal] = new State(symbols);
                queued.Add(block.Ordinal);
                pending.Enqueue(block);
            }
            while (pending.Count > 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var block = pending.Dequeue();
                queued.Remove(block.Ordinal);
                var state = incoming[block.Ordinal]!.Clone();
                foreach (var operation in block.Operations)
                    Visit(operation, state);
                if (block.BranchValue is { } branch)
                    Visit(branch, state);
                Propagate(block.FallThroughSuccessor?.Destination, state);
                Propagate(block.ConditionalSuccessor?.Destination, state);
            }

            void Propagate(BasicBlock? destination, State state)
            {
                if (destination is null || !destination.IsReachable)
                    return;
                bool changed;
                if (incoming[destination.Ordinal] is { } previous)
                    changed = previous.Join(state);
                else
                {
                    incoming[destination.Ordinal] = state.Clone();
                    changed = true;
                }
                if (changed && queued.Add(destination.Ordinal))
                    pending.Enqueue(destination);
            }
        }

        private static bool IsHandlerEntry(BasicBlock block)
        {
            if (!block.IsReachable)
                return false;
            for (var region = block.EnclosingRegion; region is not null; region = region.EnclosingRegion)
                if (region.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.Filter or ControlFlowRegionKind.Finally &&
                    region.FirstBlockOrdinal == block.Ordinal)
                    return true;
            return false;
        }

        private void Visit(IOperation operation, State state)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            switch (operation)
            {
                case IAnonymousFunctionOperation or ILocalFunctionOperation or IFlowAnonymousFunctionOperation:
                    return;
                case IFlowCaptureOperation capture:
                    Visit(capture.Value, state);
                    state.SetCapture(capture.Id, GetOrigin(capture.Value, state), MutationOrigin(capture.Value, state));
                    return;
                case IVariableDeclaratorOperation variable:
                    if (variable.Initializer is { } initializer)
                    {
                        Visit(initializer.Value, state);
                        state.Set(variable.Symbol, GetOrigin(initializer.Value, state));
                        if (variable.Symbol.RefKind == RefKind.Ref)
                            state.SetRefLocation(variable.Symbol, MutationOrigin(initializer.Value, state));
                    }
                    return;
                case ISimpleAssignmentOperation assignment:
                    Visit(assignment.Target, state);
                    var target = MutationOrigin(assignment.Target, state);
                    Visit(assignment.Value, state);
                    if (!assignment.IsRef)
                        Report(assignment.Target, target);
                    Assign(assignment.Target, GetOrigin(assignment.Value, state), state,
                        assignment.IsRef ? MutationOrigin(assignment.Value, state) : null);
                    return;
                case ICompoundAssignmentOperation assignment:
                    Visit(assignment.Target, state);
                    var compoundTarget = MutationOrigin(assignment.Target, state);
                    Visit(assignment.Value, state);
                    Report(assignment.Target, compoundTarget);
                    return;
                case IIncrementOrDecrementOperation increment:
                    Visit(increment.Target, state);
                    Report(increment.Target, MutationOrigin(increment.Target, state));
                    return;
                case IEventAssignmentOperation assignment:
                    Visit(assignment.EventReference, state);
                    var eventTarget = MutationOrigin(assignment.EventReference, state);
                    Visit(assignment.HandlerValue, state);
                    Report(assignment.EventReference, eventTarget);
                    return;
                case IDeconstructionAssignmentOperation deconstruction:
                    Visit(deconstruction.Target, state);
                    Visit(deconstruction.Value, state);
                    AssignDeconstruction(deconstruction.Target, deconstruction.Value, state);
                    return;
                case IInvocationOperation invocation:
                    VisitInvocation(invocation, state);
                    return;
                case IArgumentOperation argument:
                    Visit(argument.Value, state);
                    if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out)
                        Report(argument, MutationOrigin(argument.Value, state));
                    return;
                case IIsPatternOperation pattern:
                    Visit(pattern.Value, state);
                    AssignPattern(pattern.Pattern, GetOrigin(pattern.Value, state), state);
                    return;
            }
            foreach (var child in operation.ChildOperations)
                Visit(child, state);
        }

        private void VisitInvocation(IInvocationOperation invocation, State state)
        {
            if (invocation.Instance is { } instance)
                Visit(instance, state);
            var receiverOrigin = GetOrigin(invocation.Instance, state);
            foreach (var argument in invocation.Arguments)
                Visit(argument, state);

            if (IsCollectionMutation(invocation.TargetMethod))
                Report(invocation, receiverOrigin);
            if (MetadataName(invocation.TargetMethod.ContainingType) == "System.Array")
            {
                foreach (var argument in invocation.Arguments)
                {
                    var parameter = argument.Parameter?.Name;
                    bool writes = invocation.TargetMethod.Name switch
                    {
                        "Clear" or "Fill" or "Reverse" => parameter == "array",
                        "Sort" => parameter is "array" or "keys" or "items",
                        "Copy" or "ConstrainedCopy" => parameter == "destinationArray",
                        _ => false
                    };
                    if (writes)
                        Report(invocation, GetOrigin(argument.Value, state));
                }
            }

            var readOrigin = ManagedReadOrigin(invocation.TargetMethod);
            foreach (var argument in invocation.Arguments)
            {
                if (argument.Parameter?.RefKind != RefKind.Out)
                    continue;
                // TryGet borrows an object; other out calls replace the local with an unknown value.
                Assign(argument.Value, IsTryRead(invocation.TargetMethod) ? readOrigin : default, state);
            }
        }

        private Origin GetOrigin(IOperation? operation, State state)
        {
            switch (operation)
            {
                case null:
                    return default;
                case ILocalReferenceOperation local:
                    return state.Get(local.Local);
                case IParameterReferenceOperation parameter:
                    return state.Get(parameter.Parameter);
                case IFlowCaptureReferenceOperation capture:
                    return state.Capture(capture.Id);
                case IConversionOperation conversion:
                    return GetOrigin(conversion.Operand, state);
                case IParenthesizedOperation parenthesized:
                    return GetOrigin(parenthesized.Operand, state);
                case IObjectCreationOperation or IArrayCreationOperation or IAnonymousObjectCreationOperation:
                    return Origin.Fresh;
                case IFieldReferenceOperation field:
                {
                    var owner = GetOrigin(field.Instance, state);
                    if (owner.IsBorrowed || owner.IsFresh)
                        return owner;
                    return state.Get(field.Field);
                }
                case IPropertyReferenceOperation property:
                {
                    var lookup = LookupOrigin(property.Property.ContainingType);
                    if (property.Property.IsIndexer && lookup.IsBorrowed)
                        return lookup;
                    var owner = GetOrigin(property.Instance, state);
                    if (owner.IsBorrowed || owner.IsFresh)
                        return owner;
                    return symbols.ManagedOrigin(property.Type);
                }
                case IArrayElementReferenceOperation element:
                    return GetOrigin(element.ArrayReference, state);
                case IInvocationOperation invocation:
                {
                    var read = ManagedReadOrigin(invocation.TargetMethod);
                    if (read.IsBorrowed && !IsTryRead(invocation.TargetMethod))
                        return read;
                    if (invocation.TargetMethod.Name == "GetEnumerator" && IsStandardCollection(invocation.TargetMethod.ContainingType))
                        return GetOrigin(invocation.Instance, state);
                    return default;
                }
                case ISimpleAssignmentOperation assignment:
                    return GetOrigin(assignment.Value, state);
                case IConditionalOperation conditional:
                    return GetOrigin(conditional.WhenTrue, state).Join(GetOrigin(conditional.WhenFalse, state));
                case ICoalesceOperation coalesce:
                    return GetOrigin(coalesce.Value, state).Join(GetOrigin(coalesce.WhenNull, state));
                case IConditionalAccessOperation conditional:
                    return GetOrigin(conditional.Operation, state);
                case IConditionalAccessInstanceOperation instance:
                    for (var parent = instance.Parent; parent is not null; parent = parent.Parent)
                        if (parent is IConditionalAccessOperation access)
                            return GetOrigin(access.Operation, state);
                    return default;
                case IDeclarationExpressionOperation declaration:
                    return GetOrigin(declaration.Expression, state);
                default:
                    return default;
            }
        }

        private Origin MutationOrigin(IOperation operation, State state)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    return MutationOrigin(conversion.Operand, state);
                case IParenthesizedOperation parenthesized:
                    return MutationOrigin(parenthesized.Operand, state);
                case IFieldReferenceOperation field when field.Instance is { } instance:
                    return instance.Type?.IsReferenceType == true
                        ? GetOrigin(instance, state) : SharedValueOrigin(instance, state);
                case IPropertyReferenceOperation property when property.Instance is { } instance:
                    // Replacing a lookup slot is already represented by its declared write access.
                    if (LookupOrigin(property.Property.ContainingType).IsBorrowed)
                        return default;
                    return instance.Type?.IsReferenceType == true
                        ? GetOrigin(instance, state) : SharedValueOrigin(instance, state);
                case IArrayElementReferenceOperation array:
                    return GetOrigin(array.ArrayReference, state);
                case IEventReferenceOperation @event when @event.Instance is { } instance:
                    return instance.Type?.IsReferenceType == true
                        ? GetOrigin(instance, state) : SharedValueOrigin(instance, state);
                case ILocalReferenceOperation { Local.RefKind: RefKind.Ref } local:
                    return state.RefLocation(local.Local);
                case IFlowCaptureReferenceOperation capture:
                    return state.Location(capture.Id);
                default:
                    return default;
            }
        }

        private Origin SharedValueOrigin(IOperation operation, State state)
        {
            return operation switch
            {
                IFieldReferenceOperation or IArrayElementReferenceOperation => MutationOrigin(operation, state),
                IPropertyReferenceOperation property when property.Property.ReturnsByRef || property.Property.ReturnsByRefReadonly
                    => MutationOrigin(property, state),
                ILocalReferenceOperation { Local.RefKind: RefKind.Ref } local => state.RefLocation(local.Local),
                IFlowCaptureReferenceOperation capture => state.Location(capture.Id),
                IConversionOperation conversion => SharedValueOrigin(conversion.Operand, state),
                IParenthesizedOperation parenthesized => SharedValueOrigin(parenthesized.Operand, state),
                _ => default
            };
        }

        private void Assign(IOperation target, Origin origin, State state, Origin? refLocation = null)
        {
            switch (target)
            {
                case ILocalReferenceOperation local:
                    state.Set(local.Local, origin);
                    // Writing through a ref changes its value, but only a ref assignment changes its storage.
                    if (refLocation is { } location)
                        state.SetRefLocation(local.Local, location);
                    break;
                case IParameterReferenceOperation parameter:
                    state.Set(parameter.Parameter, origin);
                    break;
                case IFieldReferenceOperation field when field.Instance is null or IInstanceReferenceOperation:
                    state.Set(field.Field, origin);
                    break;
                case IDeclarationExpressionOperation declaration:
                    Assign(declaration.Expression, origin, state, refLocation);
                    break;
                case IConversionOperation conversion:
                    Assign(conversion.Operand, origin, state, refLocation);
                    break;
            }
        }

        private void AssignDeconstruction(IOperation target, IOperation value, State state)
        {
            var assignments = new List<(IOperation Target, Origin Value, Origin Location)>();
            Collect(target, value);
            // All tuple values are evaluated before any target changes, including nested swaps.
            foreach (var assignment in assignments)
            {
                Report(assignment.Target, assignment.Location);
                Assign(assignment.Target, assignment.Value, state);
            }

            void Collect(IOperation destination, IOperation source)
            {
                while (source is IConversionOperation conversion)
                    source = conversion.Operand;
                if (destination is IDeclarationExpressionOperation declaration)
                {
                    Collect(declaration.Expression, source);
                    return;
                }
                if (destination is ITupleOperation targets)
                {
                    var values = source as ITupleOperation;
                    for (int i = 0; i < targets.Elements.Length; i++)
                        Collect(targets.Elements[i], values?.Elements.Length == targets.Elements.Length ? values.Elements[i] : source);
                    return;
                }
                assignments.Add((destination, GetOrigin(source, state), MutationOrigin(destination, state)));
            }
        }

        private void AssignPattern(IPatternOperation pattern, Origin origin, State state)
        {
            if (pattern is IDeclarationPatternOperation { DeclaredSymbol: { } declared })
                state.Set(declared, origin);
            if (pattern is IRecursivePatternOperation { DeclaredSymbol: { } recursive })
                state.Set(recursive, origin);
            foreach (var child in pattern.ChildOperations)
            {
                if (child is IPatternOperation nested)
                    AssignPattern(nested, origin, state);
                else if (child is IPropertySubpatternOperation property)
                    AssignPattern(property.Pattern, origin, state);
            }
        }

        private Origin ManagedReadOrigin(IMethodSymbol method)
        {
            var lookup = LookupOrigin(method.ContainingType);
            if (lookup.IsBorrowed && method.Name == "TryGet")
                return lookup;
            if (method.TypeArguments.Length != 1)
                return default;
            if (MetadataName(method.ContainingType) == "Paradise.ECS.WorldEntity" && method.Name is "Get" or "TryGet")
                return symbols.ManagedOrigin(method.TypeArguments[0]);
            if (method.Name is "GetManaged" or "TryGetManaged" &&
                (MetadataName(method.ContainingType) == "Paradise.ECS.IManagedWorld" ||
                 method.ContainingType.AllInterfaces.Any(i => MetadataName(i) == "Paradise.ECS.IManagedWorld")))
                return symbols.ManagedOrigin(method.TypeArguments[0]);
            return default;
        }

        private Origin LookupOrigin(INamedTypeSymbol type)
            => MetadataName(type) is "Paradise.ECS.ManagedLookup`1" or "Paradise.ECS.ReadOnlyManagedLookup`1"
                ? symbols.ManagedOrigin(type.TypeArguments[0]) : default;

        private static bool IsTryRead(IMethodSymbol method) => method.Name is "TryGet" or "TryGetManaged";

        private void Report(IOperation operation, Origin origin)
        {
            if (!origin.IsBorrowed)
                return;
            var span = operation.Syntax.Span;
            if (!_reported.Add((span.Start, span.Length)))
                return;
            var components = string.Join(", ", origin.Roots.Select(t => t.Name).OrderBy(name => name, StringComparer.Ordinal));
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.ManagedComponentMutationInSystem,
                operation.Syntax.GetLocation(), components, system.Name));
        }
    }

    private static string MetadataName(INamedTypeSymbol type)
        => type.ContainingNamespace.ToDisplayString() + "." + type.OriginalDefinition.MetadataName;

    private static bool IsStandardCollection(INamedTypeSymbol type)
        => type.ContainingNamespace.ToDisplayString() is "System.Collections" or "System.Collections.Generic" or "System.Collections.ObjectModel";

    private static bool IsCollectionMutation(IMethodSymbol method)
    {
        if (IsKnownMutation(MetadataName(method.ContainingType), method.Name))
            return true;
        foreach (var contract in method.ContainingType.AllInterfaces)
        {
            if (!IsKnownMutation(MetadataName(contract), method.Name))
                continue;
            foreach (var member in contract.GetMembers(method.Name).OfType<IMethodSymbol>())
                if (SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(member), method))
                    return true;
        }
        return false;
    }

    private static bool IsKnownMutation(string type, string method) => type switch
    {
        "System.Collections.Generic.ICollection`1" => method is "Add" or "Clear" or "Remove",
        "System.Collections.IList" => method is "Add" or "Clear" or "Insert" or "Remove" or "RemoveAt",
        "System.Collections.Generic.IList`1" => method is "Insert" or "RemoveAt",
        "System.Collections.Generic.IDictionary`2" or "System.Collections.IDictionary" => method is "Add" or "Clear" or "Remove",
        "System.Collections.Generic.ISet`1" => method is "Add" or "ExceptWith" or "IntersectWith" or "SymmetricExceptWith" or "UnionWith",
        "System.Collections.Generic.List`1" or "System.Collections.ArrayList" => method is "Add" or "AddRange" or "Clear" or "Insert" or "InsertRange" or "Remove" or "RemoveAll" or "RemoveAt" or "RemoveRange" or "Reverse" or "Sort" or "TrimExcess" or "EnsureCapacity",
        "System.Collections.Generic.Dictionary`2" or "System.Collections.Hashtable" => method is "Add" or "Clear" or "Remove" or "TryAdd" or "EnsureCapacity" or "TrimExcess",
        "System.Collections.Generic.HashSet`1" => method is "Add" or "Remove" or "Clear" or "ExceptWith" or "IntersectWith" or "SymmetricExceptWith" or "UnionWith" or "RemoveWhere" or "EnsureCapacity" or "TrimExcess",
        "System.Collections.Generic.Queue`1" or "System.Collections.Queue" => method is "Enqueue" or "Dequeue" or "TryDequeue" or "Clear" or "EnsureCapacity" or "TrimExcess",
        "System.Collections.Generic.Stack`1" or "System.Collections.Stack" => method is "Push" or "Pop" or "TryPop" or "Clear" or "EnsureCapacity" or "TrimExcess",
        "System.Collections.Generic.LinkedList`1" => method is "AddAfter" or "AddBefore" or "AddFirst" or "AddLast" or "Remove" or "RemoveFirst" or "RemoveLast" or "Clear",
        "System.Collections.ObjectModel.Collection`1" => method is "Add" or "Clear" or "Insert" or "Remove" or "RemoveAt",
        "System.Collections.ObjectModel.ObservableCollection`1" => method is "Move" or "Add" or "Clear" or "Insert" or "Remove" or "RemoveAt",
        "System.Text.StringBuilder" => method is "Append" or "AppendLine" or "AppendFormat" or "AppendJoin" or "Clear" or "Insert" or "Remove" or "Replace",
        "System.Array" => method is "SetValue",
        _ => false
    };
}
