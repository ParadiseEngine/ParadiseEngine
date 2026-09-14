# Managed components

`Paradise.ECS.Managed` stores per-entity managed objects alongside ordinary ECS components.
Reference the package and declare a sealed partial class. The ECS generator provides its
identity, an unmanaged handle component, a managed registry, and the `World`/`SharedWorld`
aliases. No reflection or runtime type discovery is required.

```csharp
using Paradise.ECS;

[ManagedComponent]
public sealed partial class DisplayName
{
    public string Text { get; init; } = "";
}
```

Create worlds through the generated factory, then use the managed API:

```csharp
using var shared = SharedWorldFactory.Create();
var world = shared.CreateWorld();
var entity = world.Spawn();
world.AddManaged(entity, new DisplayName { Text = "Merchant" });
var name = world.GetManaged<DisplayName>(entity);
world.SetManaged(entity, new DisplayName { Text = "Traveller" });
world.RemoveManaged<DisplayName>(entity);
```

Generated registries use the project's root namespace, matching the existing ECS component generator.
`[ManagedComponent("guid", Id = 12)]` supports stable identity and explicit component IDs.
Managed slots and ordinary components share the same component ID space.

## Bound entity access

`WorldEntity` binds a world and an entity handle so ordinary application code can access
unmanaged components, tags, and managed components through the same methods:

```csharp
[Component]
public partial struct Health
{
    public int Value;
}

var character = new WorldEntity(world, world.Spawn());
character.Add(new Health { Value = 100 });
character.Add<DisplayName>(); // Present with a null value.
character.Set(new DisplayName { Text = "Merchant" });

var health = character.Get<Health>(); // Copies the unmanaged value.
health.Value -= 10;
character.Set(health);
var name = character.Get<DisplayName>(); // Returns the stored object reference.

if (character.TryGet<DisplayName>(out var displayName))
{
    // The component is present; displayName can still be null.
}
```

`Add<T>()`, `Has<T>()` and `Remove<T>()` accept all three component kinds.
For a tag, `Add<Selected>()`, `Has<Selected>()` and `Remove<Selected>()` use the tag bits.
`Add<T>(value)`, `Get<T>()`, `Set<T>(value)` and `TryGet<T>(out value)` apply to unmanaged and managed
components; tags carry presence only. `Add<T>()` initializes an unmanaged component to its
default value and a managed component to null. `GetRef<T>()` returns a writable reference
for an unmanaged component when a copy is inconvenient.
Empty marker components have no stored bytes: value access checks presence, and `GetRef<T>()`
rejects them because there is no storage to reference.

The wrapper exposes `World`, `Entity`, `IsAlive`, and `Despawn()`. It is an ordinary struct
that can be stored in fields and collections. Each operation resolves the entity's current
location, so the wrapper remains usable across archetype moves. Its entity handle still
expires on despawn, and retaining a wrapper does not extend the lifetime of the world's
resources. References returned by `GetRef<T>()` are invalidated by structural changes that
move their storage; obtain a new reference after such a change.

Generated static interface dispatch selects the appropriate component API without reflection.
The existing `WorldEntity<TMask, TConfig>` query view is unchanged. Systems continue to use
injected component fields and managed lookups to declare their scheduling dependencies.

## Presence and lifetime

Presence belongs to the entity's archetype. `AddManaged<T>(entity, null)` creates a present
component whose value is null. `HasManaged<T>` and `TryGetManaged<T>` return true for that
component; the latter returns null through its out parameter. `GetManaged<T>` throws when the
entity is dead or the component is absent. `SetManaged` replaces an existing value;
`RemoveManaged` removes presence as well as releasing the world's reference.

Creating an entity from a mask containing a managed slot gives it a null value. Required
managed filters also participate in generated `IComponentSet.CollectComponentTypes`, so
declare-then-bind entity construction works with managed components.

Despawn, overwrite, component removal, archetype moves and `Clear` maintain slot ownership.
Vacated chunk positions are cleared so a later entity cannot inherit a stale handle. Objects
are released to the GC; the world does **not** call `IDisposable.Dispose` on component values.
Shared references and snapshots can still own those objects. Resource disposal belongs to
the application.

Structural changes must go through `ManagedWorld`. Its `Inner`, entity manager and chunk
memory expose the same low-level capabilities as the core world; mutating managed handles
through them bypasses lifetime bookkeeping. Generated slot types are implementation details.
Raw writes through the wrapper accept only zero-valued slot payloads and reject forged handles.

Each `ManagedWorld` must exclusively own its inner world's mutable entity, archetype, and
event state. Custom inner worlds, including value-type adapters, must preserve that ownership
and expose the underlying world's stable `ArchetypeRegistry`. Shared chunk allocation and
`SharedArchetypeMetadata` are supported. Copy delegates must mutate only the destination.

## Snapshots

`snapshot.CopyFrom(world)` copies unmanaged chunk bytes and the matching per-world managed
stores. Choose the policy on each class:

| Policy | Destination value | Cost and contract |
| --- | --- | --- |
| `Reference` (default) | The same object | Reuses storage; observes object contents at read time, not copy time. Prefer immutable objects or application-controlled mutation. |
| `Clone` | A clone of every non-null value | Calls `IManagedClone<T>.Clone` on each copy; isolation and allocations depend on that implementation. |
| `Skip` | Null | Preserves component presence and handle allocation state; suitable for transient caches. |

```csharp
[ManagedComponent(Snapshot = ManagedSnapshot.Clone)]
public sealed partial class PlannerState : IManagedClone<PlannerState>
{
    public int Target { get; set; }
    public static PlannerState Clone(PlannerState source) => new() { Target = source.Target };
}
```

Clone methods must return a non-null value and must copy any nested mutable data that needs
isolation. `CopyFrom` rejects wrappers that share an inner archetype registry before invoking
the copy delegate, preserving the source. A failure during the inner copy or a managed clone
clears the destination so copied chunk handles cannot resolve against an unrelated old store.
Source objects remain owned by the source world.

Free slots and allocation order follow the source across copies, including `Skip` snapshots
that are later recycled as writable worlds. Repeated reference/skip copies reuse destination
arrays after capacity grows. Handle values are deterministic when allocation order is
deterministic. Managed object contents are outside the ECS byte-for-byte determinism contract.
The underlying core world currently reallocates archetype objects during copying; array reuse
in the managed extension does not make the complete world copy allocation-free.

## Deferred commands and tags

Use the ordinary command buffer during system execution:

```csharp
var entity = commands.Spawn();
commands.AddManaged(entity, new DisplayName { Text = "Merchant" });
commands.SetManaged(entity, new DisplayName { Text = "Traveller" });
commands.RemoveManaged<DisplayName>(entity);
```

References are staged on that buffer and released by `Clear` or `Dispose`, including when
playback fails. Placeholder remapping and command ordering match unmanaged operations. A
recorded object is held by reference; mutations before playback are visible during playback.

Extension sinks implement one buffer-aware playback method:
`PlayExtension(EntityCommandBuffer buffer, Type opType, Entity entity, ReadOnlySpan<byte> data)`.
The buffer identifies the staging state for that recording. Byte-only handlers such as tags
can ignore it; custom sinks using the former three-argument signature must add this parameter.
Both recording and playback use `GetOrCreateExtensionState<TState>()`; valid recorded commands
reuse their existing state during playback. Managed playback validates the staged index and type
before changing the world, including when malformed commands encounter empty staging state.

Referencing `Paradise.ECS.Tag` and declaring tags composes the managed wrapper around the tagged
world automatically. Generated `AddTag`, `RemoveTag`, `HasTag` and `GetTags` forwarding keeps
the common tag API available. Managed and tag extension commands can be interleaved in one
buffer. Use `Inner` for additional tag-specific diagnostics, without bypassing managed
structural operations.

## Queries and systems

`[WithManaged<T>]`, `[WithoutManaged<T>]` and `[WithManagedAny<T>]` filter by presence, independent
of whether an object is null. They do not expose handles or managed spans.

```csharp
[Queryable]
[WithManaged<DisplayName>]
public readonly ref partial struct NamedEntity;

public ref partial struct RenameSystem : IWorldSystem
{
    public ManagedLookup<DisplayName> Names;
    // Obtain target entities through normal ECS components or query access.
    public void Execute() { }
}
```

`ManagedLookup<T>` provides an indexer, `Has`, `TryGet`, `Set` and `TrySet` for existing
components. `ReadOnlyManagedLookup<T>` provides the read operations. Lookup fields contribute
the managed slot ID to the scheduler's access masks and participate in `[SingleWriter]`.
Writable lookups require an `IWorldSystem`: one invocation per run prevents parallel chunk
jobs from racing on a type's slot allocator. Systems writing the same managed type are
ordered by the scheduler. Read-only lookups can also be injected into entity and chunk systems.

With `[assembly: SnapshotReadSystems]`, read-only lookups bind to the read world; `[CurrentTick]`
binds them to the current world and declares the corresponding scheduling dependency.
Read-only access does not make a mutable class immutable. Under `Reference`, snapshots share
the same object and the application must coordinate mutations across readers and publishers.

Managed classes cannot be requested as component refs, spans, chunk columns or segments.
Keep bulk numeric data in unmanaged components. Managed structural operations and direct
slot allocation follow the world's single-owner model; the concurrent-world extension is
outside this package's scope.

## In-place mutation warnings

`ManagedComponentMutationAnalyzer` ships with the source generator in `Paradise.ECS` and activates when
`Paradise.ECS.Managed` is referenced. Its `PECS3014` diagnostic warns about detectable in-place
mutations of managed component objects in ECS systems, including writes through local aliases
and nested collections. The default severity is warning: it makes shared-object mutations
visible during development while leaving room for applications with explicit ownership rules.

`ReadOnlyManagedLookup<T>` makes slot access read-only; it does not make the returned object
or its nested state immutable. A `Clone` snapshot policy does not establish exclusive ownership
of an object, either. Other references can still observe its mutations, and clone implementations
can retain shared nested objects.

Prefer replacing the stored reference with a freshly constructed value. For example, given
this component and a writable `ManagedLookup<CounterState> Counters` field in an `IWorldSystem`:

```csharp
[ManagedComponent]
public sealed partial class CounterState
{
    public int Count { get; set; }
    public string Label { get; set; } = "";
}
```

Mutating the existing object reports the warning:

```csharp
var current = Counters[target]!;
current.Count++; // PECS3014: changes the object visible to other reference holders.
```

Replace it while preserving the component's other state:

```csharp
var current = Counters[target]!;
Counters.Set(target, new CounterState
{
    Count = current.Count + 1,
    Label = current.Label
});
```

An injected command buffer can defer the same replacement with `commands.SetManaged(target,
new CounterState { ... })`. Copy all state that should survive the replacement, and copy nested
mutable data when the new value needs independent ownership.

The analysis follows local aliases through ordinary branches and loops. It does not follow
aliases across helper or callback boundaries, heap storage, or exception-handler edges.
Attaching a fresh object and then mutating the original local is also outside the current
ownership tracking. Arbitrary mutator methods and reflection are not analyzed; absence of
a warning is not proof that an object is immutable or exclusively owned.

Configure the diagnostic through the standard `.editorconfig` setting:

```ini
[*.cs]
dotnet_diagnostic.PECS3014.severity = warning
```

Use `error` to require resolution or suppression before a successful build, or `none` to disable
the diagnostic. Consumer projects that treat warnings as errors can also promote the default
warning to a build failure. For an intentional mutation with established ownership, suppress
only the relevant code and record why it is safe:

```csharp
#pragma warning disable PECS3014 // This instance has one owner and no snapshot or other readers.
current.Count++;
#pragma warning restore PECS3014
```

## Validation and rollout

Runtime and generator suites cover slot recycling, raw/builder access, snapshot policies,
deferred playback, tag composition, filters, access masks and snapshot binding. The standalone
NativeAOT regression exercises both plain-managed and tagged-managed aliases, including
`WorldEntity` dispatch across component kinds, archetype moves, and entity reuse:

```bash
dotnet test --project src/Paradise.ECS.Managed.Test/Paradise.ECS.Managed.Test.csproj
dotnet test --project src/Paradise.ECS.Generators.Test/Paradise.ECS.Generators.Test.csproj
dotnet publish tools/ecs-managed-aot/ManagedAotSmoke.csproj -c Release -o /tmp/managed-aot
/tmp/managed-aot/ManagedAotSmoke
```

The package is included in the repository's version-tag publishing workflow. Consumer
migrations, including ShiningPie's physics/planner ownership and per-entity text, must use a
published matching engine version before their repository changes ship.
