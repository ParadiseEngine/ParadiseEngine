# Shared component initialization

`IComponentWriter` is the write-only contract shared by `IWorld` and `EntityCommandBuffer`.
It supports adding unmanaged components, without exposing queries, component references,
entity lifecycle, managed objects or tags. Plain, tagged and managed worlds implement it
through `IWorld`. The concurrent world implements `IComponentWriter` directly and retains its
existing structural-change locking. A multi-component initializer is not an atomic transaction.

An initializer can use one implementation for construction and deferred simulation commands:

```csharp
static void Initialize<TWriter>(TWriter writer, Entity entity, float health)
    where TWriter : IComponentWriter
{
    writer.AddComponent(entity, new Health { Value = health });
    writer.AddComponent(entity, new MaxHealth { Value = health });
}

var immediate = world.Spawn();
Initialize(world, immediate, 100f);

var pending = commands.Spawn();
Initialize(commands, pending, 100f);
commands.Playback(world);
var actual = commands.Resolve(pending);
```

The abstraction shares **what is written**, not **when it becomes visible**:

- World additions happen immediately and retain normal structural-change guards.
- ECB additions are recorded; the destination world is changed only at playback.
- A placeholder is valid only in the buffer that created it. `Resolve` returns its real identity
  after playback; arbitrary entity fields embedded inside component values are not remapped.
  Initializers that write self identities must handle that separately.
- A managed-world write still passes through the outer world's structural bookkeeping. The
  interface does not bypass ownership, tags or managed component lifetime handling.
- Existing explicit `IWorld.AddComponent` implementations inherit the writer contract through
  a forwarding default implementation; their declaration sites do not need to move.

Use the generic constraint when keeping the writer's concrete type matters. An
`IComponentWriter` parameter is also supported; neither form permits immediate readback.
