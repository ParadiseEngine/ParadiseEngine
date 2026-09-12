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
isolation. A clone failure clears the destination so copied chunk handles cannot resolve
against an unrelated old store. Source objects remain owned by the source world.

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
