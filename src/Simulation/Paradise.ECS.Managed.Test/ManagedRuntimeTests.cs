using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.ECS.Managed.Test;

[ManagedComponent]
public sealed partial class RuntimePayload
{
    public int Value { get; set; }
}

[ManagedComponent(Snapshot = ManagedSnapshot.Clone)]
public sealed partial class ClonePayload : IManagedClone<ClonePayload>
{
    public int Value { get; set; }
    public bool ThrowOnClone { get; set; }
    static ClonePayload IManagedClone<ClonePayload>.Clone(ClonePayload source)
        => source.ThrowOnClone ? throw new InvalidOperationException("Clone failed.") : new ClonePayload { Value = source.Value };
}

[ManagedComponent(Snapshot = ManagedSnapshot.Skip)]
public sealed partial class SkipPayload
{
    public int Value { get; set; }
}

[Component]
public partial struct RuntimeNumber
{
    public int Value;
}

[Tag]
public partial struct RuntimeTag;

public sealed class ManagedRuntimeTests
{
    [Test]
    public async Task NullValueRetainsPresence_AndLookupReplacesObjects()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.Spawn();
        world.AddManaged<RuntimePayload>(entity, null);
        await Assert.That(world.HasManaged<RuntimePayload>(entity)).IsTrue();
        await Assert.That(world.TryGetManaged<RuntimePayload>(entity, out var empty)).IsTrue();
        await Assert.That(empty).IsNull();

        var payload = new RuntimePayload { Value = 7 };
        var lookup = new ManagedLookup<RuntimePayload>(world);
        lookup[entity] = payload;
        await Assert.That(ReferenceEquals(payload, lookup[entity])).IsTrue();
        lookup.Set(entity, null);
        await Assert.That(lookup.Has(entity)).IsTrue();
        await Assert.That(lookup[entity]).IsNull();
        await Assert.That(ReadHandle(world, entity, RuntimePayload.SlotTypeId)).IsEqualTo(0);
    }

    [Test]
    public async Task SlotReuseIsDeterministic_AndSeparateAcrossWorldsAndTypes()
    {
        using var shared = SharedWorldFactory.Create();
        var left = shared.CreateWorld();
        var right = shared.CreateWorld();
        for (int i = 0; i < 48; i++)
        {
            var a = left.Spawn();
            var b = right.Spawn();
            left.AddManaged(a, new RuntimePayload { Value = i });
            right.AddManaged(b, new RuntimePayload { Value = i });
            await Assert.That(ReadHandle(left, a, RuntimePayload.SlotTypeId)).IsEqualTo(ReadHandle(right, b, RuntimePayload.SlotTypeId));
            left.AddManaged(a, new SkipPayload { Value = -i });
            if (i % 2 == 0)
            {
                left.RemoveManaged<RuntimePayload>(a);
                right.RemoveManaged<RuntimePayload>(b);
                await Assert.That(left.GetManaged<SkipPayload>(a)!.Value).IsEqualTo(-i);
            }
        }
    }

    [Test]
    public async Task DespawnSwapClearsTail_AndKeepsSwapVictimAlive()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var first = world.Spawn();
        var last = world.Spawn();
        world.AddManaged(first, new RuntimePayload { Value = 1 });
        var payload = new RuntimePayload { Value = 2 };
        world.AddManaged(last, payload);
        var mask = ComponentMask.Empty.Set(RuntimePayload.SlotTypeId);
        world.Despawn(first);
        var reused = world.CreateEntity(in mask);
        await Assert.That(world.GetManaged<RuntimePayload>(reused)).IsNull();
        await Assert.That(ReferenceEquals(payload, world.GetManaged<RuntimePayload>(last))).IsTrue();
        world.RemoveManaged<RuntimePayload>(last);
        world.SetManaged(reused, new RuntimePayload { Value = 3 });
        await Assert.That(world.GetManaged<RuntimePayload>(reused)!.Value).IsEqualTo(3);
    }

    [Test]
    public async Task RemovingAcrossAChunkBoundaryNeverResurrectsFreedHandles()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var mask = ComponentMask.Empty.Set(RuntimePayload.SlotTypeId);
        var first = world.CreateEntity(in mask);
        world.SetManaged(first, new RuntimePayload { Value = 1 });
        var layout = world.ArchetypeRegistry.GetById(world.GetLocation(first).ArchetypeId)!.Layout;
        int capacity = layout.EntitiesPerChunk;
        Entity last = first;
        for (int i = 1; i <= capacity; i++)
        {
            last = world.CreateEntity(in mask);
            world.SetManaged(last, new RuntimePayload { Value = i + 1 });
        }

        world.Despawn(first);
        await Assert.That(world.GetManaged<RuntimePayload>(last)!.Value).IsEqualTo(capacity + 1);
        var reused = world.CreateEntity(in mask);
        await Assert.That(world.GetManaged<RuntimePayload>(reused)).IsNull();
        world.SetManaged(reused, new RuntimePayload { Value = -1 });
        await Assert.That(ReadHandle(world, reused, RuntimePayload.SlotTypeId)).IsEqualTo(1);
        world.Clear();
        var fresh = world.CreateEntity(in mask);
        await Assert.That(world.GetManaged<RuntimePayload>(fresh)).IsNull();
    }

    [Test]
    public async Task EveryMoveClearsVacatedTail_WithoutLosingRetainedHandles()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.Spawn();
        var payload = new RuntimePayload { Value = 4 };
        world.AddManaged(entity, payload);
        world.AddComponent(entity, new RuntimeNumber { Value = 11 });
        var managedOnly = ComponentMask.Empty.Set(RuntimePayload.SlotTypeId);
        var created = world.CreateEntity(in managedOnly);
        await Assert.That(world.GetManaged<RuntimePayload>(created)).IsNull();
        world.RemoveComponent<RuntimeNumber>(entity);
        var both = managedOnly.Set(RuntimeNumber.TypeId);
        var reused = world.CreateEntity(in both);
        await Assert.That(world.GetManaged<RuntimePayload>(reused)).IsNull();
        await Assert.That(ReferenceEquals(payload, world.GetManaged<RuntimePayload>(entity))).IsTrue();
    }

    [Test]
    public async Task OverwriteSameMaskDropsObjects_AndSwapVictimRemainsValid()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var first = world.CreateEntity(new SlotBuilder());
        var last = world.CreateEntity(new SlotBuilder());
        world.SetManaged(first, new RuntimePayload { Value = 1 });
        var victim = new RuntimePayload { Value = 2 };
        world.SetManaged(last, victim);
        world.OverwriteEntity(first, new SlotBuilder());
        await Assert.That(world.GetManaged<RuntimePayload>(first)).IsNull();
        await Assert.That(ReferenceEquals(victim, world.GetManaged<RuntimePayload>(last))).IsTrue();
        var replacement = new RuntimePayload { Value = 3 };
        world.SetManaged(first, replacement);
        await Assert.That(ReadHandle(world, first, RuntimePayload.SlotTypeId)).IsEqualTo(1);
    }

    [Test]
    public async Task ManagedBuilderEnsurePreservesExistingManagedAndUnmanagedValues()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.CreateEntity(EntityBuilder.Create().Add(new RuntimeNumber { Value = 25 }));
        var payload = new RuntimePayload { Value = 7 };
        world.AddManaged(entity, payload);
        world.AddComponents(entity, new SlotBuilder());
        await Assert.That(world.GetComponent<RuntimeNumber>(entity).Value).IsEqualTo(25);
        await Assert.That(ReferenceEquals(payload, world.GetManaged<RuntimePayload>(entity))).IsTrue();
    }

    [Test]
    public async Task ForgedBuilderIsRejectedBeforeAnyEntityMutation()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.CreateEntity(EntityBuilder.Create().Add(new RuntimeNumber { Value = 25 }));
        var payload = new RuntimePayload { Value = 8 };
        world.AddManaged(entity, payload);
        await Assert.That(() => world.CreateEntity(new SlotBuilder(999))).Throws<InvalidOperationException>();
        await Assert.That(() => world.AddComponents(entity, new SlotBuilder(999))).Throws<InvalidOperationException>();
        await Assert.That(() => world.OverwriteEntity(entity, new SlotBuilder(999))).Throws<InvalidOperationException>();
        await Assert.That(world.EntityCount).IsEqualTo(1);
        await Assert.That(world.GetComponent<RuntimeNumber>(entity).Value).IsEqualTo(25);
        await Assert.That(ReferenceEquals(payload, world.GetManaged<RuntimePayload>(entity))).IsTrue();
    }

    [Test]
    public async Task ThrowingUnmanagedBuilderPreservesManagedOwnerBeforeStructuralMutation()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.Spawn();
        var payload = new RuntimePayload();
        world.AddManaged(entity, payload);
        await Assert.That(() => world.AddComponents(entity, new ThrowingNumberBuilder())).Throws<InvalidOperationException>();
        await Assert.That(() => world.OverwriteEntity(entity, new ThrowingNumberBuilder())).Throws<InvalidOperationException>();
        await Assert.That(world.EntityCount).IsEqualTo(1);
        await Assert.That(world.HasComponent<RuntimeNumber>(entity)).IsFalse();
        await Assert.That(ReferenceEquals(payload, world.GetManaged<RuntimePayload>(entity))).IsTrue();
    }

    [Test]
    public async Task RawAndGenericSlotWritesRejectForgedHandles_AndZeroReleasesReference()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.Spawn();
        await Assert.That(() => world.AddComponentRaw(entity, RuntimePayload.SlotTypeId, BitConverter.GetBytes(42))).Throws<InvalidOperationException>();
        await Assert.That(world.HasManaged<RuntimePayload>(entity)).IsFalse();
        world.AddManaged(entity, new RuntimePayload());
        await Assert.That(() => world.SetComponentRaw(entity, RuntimePayload.SlotTypeId, BitConverter.GetBytes(42))).Throws<InvalidOperationException>();
        await Assert.That(() => world.TrySetComponent(entity, new RawSlot { Handle = 42 })).Throws<InvalidOperationException>();
        await Assert.That(() => ReadWritableSlot(world, entity)).Throws<InvalidOperationException>();
        world.SetComponentRaw(entity, RuntimePayload.SlotTypeId, BitConverter.GetBytes(0));
        await Assert.That(world.GetManaged<RuntimePayload>(entity)).IsNull();
        world.RemoveComponentRaw(entity, RuntimePayload.SlotTypeId);
        world.AddComponent(entity, default(RawSlot));
        await Assert.That(world.GetManaged<RuntimePayload>(entity)).IsNull();
    }

    [Test]
    public async Task SnapshotPoliciesAndFreeListsSurviveWorldPoolRecycling()
    {
        using var shared = SharedWorldFactory.Create();
        var write = shared.CreateWorld();
        var read = shared.CreateWorld();
        var first = write.Spawn();
        var entity = write.Spawn();
        write.AddManaged(first, new RuntimePayload());
        var reference = new RuntimePayload { Value = 12 };
        write.AddManaged(entity, reference);
        write.AddManaged(entity, new ClonePayload { Value = 21 });
        write.AddManaged(entity, new SkipPayload { Value = 31 });
        write.RemoveManaged<RuntimePayload>(first);
        for (int i = 0; i < 20; i++)
        {
            read.CopyFrom(write);
            await Assert.That(ReferenceEquals(reference, read.GetManaged<RuntimePayload>(entity))).IsTrue();
            await Assert.That(ReferenceEquals(write.GetManaged<ClonePayload>(entity), read.GetManaged<ClonePayload>(entity))).IsFalse();
            await Assert.That(read.GetManaged<ClonePayload>(entity)!.Value).IsEqualTo(21);
            await Assert.That(read.HasManaged<SkipPayload>(entity)).IsTrue();
            await Assert.That(read.GetManaged<SkipPayload>(entity)).IsNull();
            read.AddManaged(first, new RuntimePayload());
            await Assert.That(ReadHandle(read, first, RuntimePayload.SlotTypeId)).IsEqualTo(1);
            read.RemoveManaged<RuntimePayload>(first);
            read.SetManaged(entity, new SkipPayload { Value = i });
            (write, read) = (read, write);
        }
        reference.Value = 99;
        await Assert.That(write.GetManaged<RuntimePayload>(entity)!.Value).IsEqualTo(99);
    }

    [Test]
    public async Task WarmReferenceAndSkipSnapshotsAddNoAllocationsBeyondInnerWorldCopy()
    {
        using var shared = SharedWorldFactory.Create();
        var source = shared.CreateWorld();
        var target = shared.CreateWorld();
        var baseline = shared.CreateWorld().Inner;
        for (int i = 0; i < 40; i++)
        {
            var entity = source.Spawn();
            source.AddManaged(entity, new RuntimePayload { Value = i });
            source.AddManaged(entity, new SkipPayload { Value = i });
            if (i % 2 == 0)
                source.SetManaged<RuntimePayload>(entity, null);
        }

        for (int i = 0; i < 10; i++)
        {
            baseline.CopyFrom(source.Inner);
            target.CopyFrom(source);
        }

        // Core CopyFrom recreates archetypes today; compare its identical workload to isolate the slot-store cost.
        long beforeBaseline = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            baseline.CopyFrom(source.Inner);
        long baselineBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBaseline;
        long beforeManaged = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            target.CopyFrom(source);
        long managedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeManaged;
        await Assert.That(managedBytes).IsEqualTo(baselineBytes);
    }

    [Test]
    public async Task FailedCloneLeavesAnEmptyReusableDestination()
    {
        using var shared = SharedWorldFactory.Create();
        var source = shared.CreateWorld();
        var target = shared.CreateWorld();
        var entity = source.Spawn();
        source.AddManaged(entity, new ClonePayload { ThrowOnClone = true });
        target.AddManaged(target.Spawn(), new RuntimePayload());
        await Assert.That(() => target.CopyFrom(source)).Throws<InvalidOperationException>();
        await Assert.That(target.EntityCount).IsEqualTo(0);
        source.GetManaged<ClonePayload>(entity)!.ThrowOnClone = false;
        target.CopyFrom(source);
        await Assert.That(target.GetManaged<ClonePayload>(entity)).IsNotNull();
    }

    [Test]
    public async Task InvalidSnapshotSourcesDoNotClearDestination()
    {
        using var shared = SharedWorldFactory.Create();
        using var other = SharedWorldFactory.Create();
        var target = shared.CreateWorld();
        var entity = target.Spawn();
        target.AddManaged(entity, new RuntimePayload());
        await Assert.That(() => target.CopyFrom(target)).Throws<InvalidOperationException>();
        await Assert.That(() => target.CopyFrom(other.CreateWorld())).Throws<InvalidOperationException>();
        await Assert.That(target.GetManaged<RuntimePayload>(entity)).IsNotNull();
    }

    [Test]
    public async Task DeferredManagedAndTagCommandsRespectOrderAndPlaceholderRemapping()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        using var commands = new EntityCommandBuffer();
        var pending = commands.Spawn();
        commands.AddManaged(pending, new RuntimePayload { Value = 1 });
        commands.AddTag<RuntimeTag>(pending);
        commands.SetManaged(pending, new RuntimePayload { Value = 2 });
        commands.RemoveManaged<RuntimePayload>(pending);
        commands.AddManaged(pending, new RuntimePayload { Value = 3 });
        commands.Playback(world);
        var entity = commands.Resolve(pending);
        await Assert.That(world.GetManaged<RuntimePayload>(entity)!.Value).IsEqualTo(3);
        await Assert.That(world.HasTag<RuntimeTag>(entity)).IsTrue();
        commands.Clear();
        commands.RemoveTag<RuntimeTag>(entity);
        commands.SetManaged<RuntimePayload>(entity, null);
        commands.Playback(world);
        await Assert.That(world.GetManaged<RuntimePayload>(entity)).IsNull();
        await Assert.That(world.HasTag<RuntimeTag>(entity)).IsFalse();
    }

    [Test]
    public async Task ClearAndDisposeReleaseWorldAndUnplayedCommandReferences()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var worldReference = AddWeakReference(world);
        using var commands = new EntityCommandBuffer();
        var stagedReference = StageWeakReference(commands);
        world.Clear();
        commands.Clear();
        Collect();
        await Assert.That(worldReference.IsAlive).IsFalse();
        await Assert.That(stagedReference.IsAlive).IsFalse();
        var disposedReference = StageWeakReference(commands);
        commands.Dispose();
        Collect();
        await Assert.That(disposedReference.IsAlive).IsFalse();
    }

    [Test]
    public async Task RemoveDespawnAndOverwriteReleaseManagedReferences()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var removed = AddWeakReference(world);
        var removedEntity = world.GetEntity(0);
        world.RemoveManaged<RuntimePayload>(removedEntity);
        var despawned = AddWeakReference(world);
        world.Despawn(world.GetEntity(1));
        var overwritten = AddWeakReference(world);
        world.OverwriteEntity(world.GetEntity(1), EntityBuilder.Create());
        Collect();
        await Assert.That(removed.IsAlive).IsFalse();
        await Assert.That(despawned.IsAlive).IsFalse();
        await Assert.That(overwritten.IsAlive).IsFalse();
    }

    [Test]
    public async Task StaleEntitiesAndMissingComponentsRemainDistinctFromPresentNull()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.Spawn();
        await Assert.That(() => world.GetManaged<RuntimePayload>(entity)).Throws<InvalidOperationException>();
        await Assert.That(world.TryGetManaged<RuntimePayload>(entity, out _)).IsFalse();
        world.AddManaged<RuntimePayload>(entity, null);
        world.Despawn(entity);
        var replacement = world.Spawn();
        world.AddManaged<RuntimePayload>(replacement, null);
        await Assert.That(world.HasManaged<RuntimePayload>(entity)).IsFalse();
        await Assert.That(world.TryGetManaged<RuntimePayload>(replacement, out _)).IsTrue();
    }

    [Test]
    public async Task PlainInnerWorldSupportsTheSameSnapshotAndLifetimeContract()
    {
        using var shared = new SharedManagedWorld<ComponentMask, DefaultConfig, Paradise.ECS.World<ComponentMask, DefaultConfig>>(
            ComponentRegistry.Shared.TypeInfos, ManagedRegistry.TypeInfos,
            static (config, chunks, metadata) => new Paradise.ECS.World<ComponentMask, DefaultConfig>(config, metadata, chunks),
            static (target, source) => target.CopyFrom(source));
        var source = shared.CreateWorld();
        var target = shared.CreateWorld();
        var entity = source.Spawn();
        var payload = new RuntimePayload();
        source.AddManaged(entity, payload);
        target.CopyFrom(source);
        source.Despawn(entity);
        await Assert.That(ReferenceEquals(payload, target.GetManaged<RuntimePayload>(entity))).IsTrue();
    }

#if DEBUG
    [Test]
    public async Task StructuralGuardRejectsChangesBeforeReleasingManagedSlots()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.Spawn();
        var payload = new RuntimePayload();
        world.AddManaged(entity, payload);
        world.SetSystemRunInProgress(true);
        try
        {
            await Assert.That(() => world.RemoveManaged<RuntimePayload>(entity)).Throws<InvalidOperationException>();
            await Assert.That(() => world.Despawn(entity)).Throws<InvalidOperationException>();
            await Assert.That(() => world.OverwriteEntity(entity, EntityBuilder.Create())).Throws<InvalidOperationException>();
            await Assert.That(world.Clear).Throws<InvalidOperationException>();
            await Assert.That(ReferenceEquals(payload, world.GetManaged<RuntimePayload>(entity))).IsTrue();
        }
        finally
        {
            world.SetSystemRunInProgress(false);
        }
    }
#endif

    private static int ReadHandle(World world, Entity entity, ComponentId slotId)
    {
        var location = world.GetLocation(entity);
        var archetype = world.ArchetypeRegistry.GetById(location.ArchetypeId)!;
        var (chunkIndex, index) = archetype.GetChunkLocation(location.GlobalIndex);
        return MemoryMarshal.Read<int>(world.ChunkManager.GetBytes(archetype.GetChunk(chunkIndex))
            .Slice(archetype.Layout.GetBaseOffset(slotId) + index * sizeof(int), sizeof(int)));
    }

    private static int ReadWritableSlot(World world, Entity entity) => world.GetComponent<RawSlot>(entity).Handle;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddWeakReference(World world)
    {
        var value = new RuntimePayload();
        world.AddManaged(world.Spawn(), value);
        return new WeakReference(value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StageWeakReference(EntityCommandBuffer commands)
    {
        var value = new RuntimePayload();
        commands.AddManaged(commands.Spawn(), value);
        return new WeakReference(value);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private struct RawSlot : IComponent
    {
        public int Handle;
        public static ComponentId TypeId => RuntimePayload.SlotTypeId;
        public static Guid Guid => RuntimePayload.Guid;
        public static int Size => sizeof(int);
        public static int Alignment => sizeof(int);
    }

    private readonly struct SlotBuilder(int handle = 0) : IComponentsBuilder
    {
        public void CollectTypes<TMask>(ref TMask mask) where TMask : unmanaged, IBitSet<TMask>
            => mask = mask.Set(RuntimePayload.SlotTypeId).Set(RuntimeNumber.TypeId);

        public void WriteComponents<TMask, TConfig, TChunkManager>(TChunkManager chunkManager,
            ImmutableArchetypeLayout<TMask, TConfig> layout, ChunkHandle chunkHandle, int indexInChunk)
            where TMask : unmanaged, IBitSet<TMask>
            where TConfig : IConfig, new()
            where TChunkManager : IChunkManager
        {
            chunkManager.GetBytes(chunkHandle).GetRef<int>(layout.GetBaseOffset(RuntimePayload.SlotTypeId) + indexInChunk * sizeof(int)) = handle;
        }
    }

    private readonly struct ThrowingNumberBuilder : IComponentsBuilder
    {
        public void CollectTypes<TMask>(ref TMask mask) where TMask : unmanaged, IBitSet<TMask>
            => mask = mask.Set(RuntimeNumber.TypeId);

        public void WriteComponents<TMask, TConfig, TChunkManager>(TChunkManager chunkManager,
            ImmutableArchetypeLayout<TMask, TConfig> layout, ChunkHandle chunkHandle, int indexInChunk)
            where TMask : unmanaged, IBitSet<TMask>
            where TConfig : IConfig, new()
            where TChunkManager : IChunkManager
            => throw new InvalidOperationException("Builder failed.");
    }
}
