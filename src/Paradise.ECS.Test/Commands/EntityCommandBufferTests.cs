using System.Runtime.InteropServices;

namespace Paradise.ECS.Test;

/// <summary>
/// Tests for EntityCommandBuffer deferred command recording and playback.
/// </summary>
public sealed class EntityCommandBufferTests : IDisposable
{
    private static readonly DefaultConfig s_config = new();
    private readonly ChunkManager _chunkManager = ChunkManager.Create(s_config);
    private readonly SharedArchetypeMetadata<SmallBitSet<ulong>, DefaultConfig> _sharedMetadata = new(ComponentRegistry.Shared.TypeInfos, s_config);
    private readonly World<SmallBitSet<ulong>, DefaultConfig> _world;

    public EntityCommandBufferTests()
    {
        _world = new World<SmallBitSet<ulong>, DefaultConfig>(s_config, _sharedMetadata, _chunkManager);
    }

    public void Dispose()
    {
        _sharedMetadata.Dispose();
        _chunkManager.Dispose();
    }

    [Test]
    public async Task Spawn_ReturnsEntityWithRealId()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        var entity = ecb.Spawn();

        await Assert.That(entity.Id).IsGreaterThanOrEqualTo(0);
        await Assert.That(entity.IsValid).IsTrue();
    }

    [Test]
    public async Task Spawn_MultipleDeferredEntities_HaveUniqueNonNegativeIds()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        var d1 = ecb.Spawn();
        var d2 = ecb.Spawn();
        var d3 = ecb.Spawn();

        await Assert.That(d1.Id).IsGreaterThanOrEqualTo(0);
        await Assert.That(d2.Id).IsGreaterThanOrEqualTo(0);
        await Assert.That(d3.Id).IsGreaterThanOrEqualTo(0);
        await Assert.That(d1.Id).IsNotEqualTo(d2.Id);
        await Assert.That(d2.Id).IsNotEqualTo(d3.Id);
        await Assert.That(d1.Id).IsNotEqualTo(d3.Id);
    }

    [Test]
    public async Task Spawn_Playback_CreatesRealEntity()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        ecb.Spawn();

        int countBefore = _world.EntityCount;
        ecb.Playback(_world);

        await Assert.That(_world.EntityCount).IsEqualTo(countBefore + 1);
    }

    [Test]
    public async Task Spawn_MultiplePlayback_CreatesMultipleEntities()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        ecb.Spawn();
        ecb.Spawn();
        ecb.Spawn();

        int countBefore = _world.EntityCount;
        ecb.Playback(_world);

        await Assert.That(_world.EntityCount).IsEqualTo(countBefore + 3);
    }

    [Test]
    public async Task Despawn_ExistingEntity_RemovedAfterPlayback()
    {
        var entity = _world.Spawn();
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        ecb.Despawn(entity);
        ecb.Playback(_world);

        await Assert.That(_world.IsAlive(entity)).IsFalse();
    }

    [Test]
    public async Task Despawn_DeferredSpawnThenDespawn_NetZero()
    {
        int countBefore = _world.EntityCount;
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        var deferred = ecb.Spawn();
        ecb.Despawn(deferred);
        ecb.Playback(_world);

        await Assert.That(_world.EntityCount).IsEqualTo(countBefore);
    }

    [Test]
    public async Task AddComponent_OnDeferredEntity_Playback_AddsComponent()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        var deferred = ecb.Spawn();
        ecb.AddComponent(deferred, new TestPosition { X = 10, Y = 20, Z = 30 });
        ecb.Playback(_world);

        // The deferred entity now has a real, stable ID — use it directly
        await Assert.That(_world.HasComponent<TestPosition>(deferred)).IsTrue();
        var pos = _world.GetComponent<TestPosition>(deferred);
        await Assert.That(pos.X).IsEqualTo(10f);
        await Assert.That(pos.Y).IsEqualTo(20f);
        await Assert.That(pos.Z).IsEqualTo(30f);
    }

    [Test]
    public async Task AddComponent_OnExistingEntity_Playback_AddsComponent()
    {
        var entity = _world.Spawn();
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        ecb.AddComponent(entity, new TestPosition { X = 42 });
        ecb.Playback(_world);

        await Assert.That(_world.HasComponent<TestPosition>(entity)).IsTrue();
        var pos = _world.GetComponent<TestPosition>(entity);
        await Assert.That(pos.X).IsEqualTo(42f);
    }

    [Test]
    public async Task AddComponent_TagComponent_Playback_AddsTag()
    {
        var entity = _world.Spawn();
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        ecb.AddComponent<TestTag>(entity);
        ecb.Playback(_world);

        await Assert.That(_world.HasComponent<TestTag>(entity)).IsTrue();
    }

    [Test]
    public async Task AddComponent_MultipleComponentsOnSameEntity_Playback_AllPresent()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        var deferred = ecb.Spawn();
        ecb.AddComponent(deferred, new TestPosition { X = 1 });
        ecb.AddComponent(deferred, new TestVelocity { X = 2 });
        ecb.AddComponent(deferred, new TestHealth { Current = 100, Max = 200 });
        ecb.Playback(_world);

        await Assert.That(_world.HasComponent<TestPosition>(deferred)).IsTrue();
        await Assert.That(_world.HasComponent<TestVelocity>(deferred)).IsTrue();
        await Assert.That(_world.HasComponent<TestHealth>(deferred)).IsTrue();

        await Assert.That(_world.GetComponent<TestPosition>(deferred).X).IsEqualTo(1f);
        await Assert.That(_world.GetComponent<TestVelocity>(deferred).X).IsEqualTo(2f);
        var health = _world.GetComponent<TestHealth>(deferred);
        await Assert.That(health.Current).IsEqualTo(100);
        await Assert.That(health.Max).IsEqualTo(200);
    }

    [Test]
    public async Task RemoveComponent_ExistingComponent_Playback_RemovesIt()
    {
        var entity = _world.Spawn();
        _world.AddComponent(entity, new TestPosition { X = 10 });
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        ecb.RemoveComponent<TestPosition>(entity);
        ecb.Playback(_world);

        await Assert.That(_world.HasComponent<TestPosition>(entity)).IsFalse();
        await Assert.That(_world.IsAlive(entity)).IsTrue();
    }

    [Test]
    public async Task RemoveComponent_MissingComponent_Playback_Throws()
    {
        var entity = _world.Spawn();
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        ecb.RemoveComponent<TestPosition>(entity);

        await Assert.That(() => ecb.Playback(_world)).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task SetComponent_UpdatesValueAfterPlayback()
    {
        var entity = _world.Spawn();
        _world.AddComponent(entity, new TestPosition { X = 1, Y = 2, Z = 3 });
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        ecb.SetComponent(entity, new TestPosition { X = 10, Y = 20, Z = 30 });
        ecb.Playback(_world);

        var pos = _world.GetComponent<TestPosition>(entity);
        await Assert.That(pos.X).IsEqualTo(10f);
        await Assert.That(pos.Y).IsEqualTo(20f);
        await Assert.That(pos.Z).IsEqualTo(30f);
    }

    [Test]
    public async Task SetComponent_TagComponent_PlaybackSucceeds()
    {
        var entity = _world.Spawn();
        _world.AddComponent<TestTag>(entity);
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        ecb.SetComponent<TestTag>(entity);
        ecb.Playback(_world);

        await Assert.That(_world.HasComponent<TestTag>(entity)).IsTrue();
    }

    [Test]
    public async Task Mixed_SpawnAddSetDespawn_Sequence()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        var existing = _world.Spawn();
        _world.AddComponent(existing, new TestHealth { Current = 50, Max = 100 });

        var deferred = ecb.Spawn();
        ecb.AddComponent(deferred, new TestPosition { X = 5 });
        ecb.SetComponent(existing, new TestHealth { Current = 75, Max = 100 });
        ecb.Despawn(existing);

        ecb.Playback(_world);

        // Existing entity should be despawned
        await Assert.That(_world.IsAlive(existing)).IsFalse();

        // Deferred entity should exist with Position — use its real ID directly
        await Assert.That(_world.IsAlive(deferred)).IsTrue();
        await Assert.That(_world.HasComponent<TestPosition>(deferred)).IsTrue();
        await Assert.That(_world.GetComponent<TestPosition>(deferred).X).IsEqualTo(5f);
    }

    [Test]
    public async Task Mixed_MultipleDeferredEntities_IndependentState()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        var d1 = ecb.Spawn();
        var d2 = ecb.Spawn();

        ecb.AddComponent(d1, new TestPosition { X = 100 });
        ecb.AddComponent(d2, new TestPosition { X = 200 });
        ecb.AddComponent(d1, new TestVelocity { Y = 10 });

        ecb.Playback(_world);

        await Assert.That(_world.GetComponent<TestPosition>(d1).X).IsEqualTo(100f);
        await Assert.That(_world.GetComponent<TestPosition>(d2).X).IsEqualTo(200f);
        await Assert.That(_world.HasComponent<TestVelocity>(d1)).IsTrue();
        await Assert.That(_world.HasComponent<TestVelocity>(d2)).IsFalse();
    }

    [Test]
    public async Task EmptyPlayback_IsNoOp()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        int countBefore = _world.EntityCount;

        ecb.Playback(_world);

        await Assert.That(_world.EntityCount).IsEqualTo(countBefore);
    }

    [Test]
    public async Task Clear_ResetsBuffer()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        ecb.Spawn();
        ecb.Spawn();

        ecb.Clear();

        await Assert.That(ecb.CommandCount).IsEqualTo(0);
        await Assert.That(ecb.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Clear_AllowsReuse()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        ecb.Spawn();
        ecb.Playback(_world);

        int countAfterFirst = _world.EntityCount;

        ecb.Clear();
        ecb.Spawn();
        ecb.Playback(_world);

        await Assert.That(_world.EntityCount).IsEqualTo(countAfterFirst + 1);
    }

    [Test]
    public async Task CommandCount_TracksRecordedCommands()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);

        await Assert.That(ecb.CommandCount).IsEqualTo(0);
        await Assert.That(ecb.IsEmpty).IsTrue();

        ecb.Spawn();
        await Assert.That(ecb.CommandCount).IsEqualTo(1);
        await Assert.That(ecb.IsEmpty).IsFalse();

        var entity = _world.Spawn();
        ecb.AddComponent(entity, new TestPosition { X = 1 });
        await Assert.That(ecb.CommandCount).IsEqualTo(2);
    }

    [Test]
    public async Task Dispose_PreventsRecording()
    {
        var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        ecb.Dispose();

        await Assert.That(ecb.Spawn).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task Dispose_PreventsPlayback()
    {
        var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        ecb.Dispose();

        await Assert.That(() => ecb.Playback(_world)).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task AddComponentRaw_AddsComponentWithData()
    {
        var entity = _world.Spawn();
        var pos = new TestPosition { X = 7, Y = 8, Z = 9 };
        var data = MemoryMarshal.AsBytes(new ReadOnlySpan<TestPosition>(in pos));

        _world.AddComponentRaw(entity, TestPosition.TypeId, data);

        await Assert.That(_world.HasComponent<TestPosition>(entity)).IsTrue();
        var result = _world.GetComponent<TestPosition>(entity);
        await Assert.That(result.X).IsEqualTo(7f);
        await Assert.That(result.Y).IsEqualTo(8f);
        await Assert.That(result.Z).IsEqualTo(9f);
    }

    [Test]
    public async Task AddComponentRaw_TagComponent_AddsTag()
    {
        var entity = _world.Spawn();

        _world.AddComponentRaw(entity, TestTag.TypeId, ReadOnlySpan<byte>.Empty);

        await Assert.That(_world.HasComponent<TestTag>(entity)).IsTrue();
    }

    [Test]
    public async Task AddComponentRaw_DuplicateComponent_Throws()
    {
        var entity = _world.Spawn();
        _world.AddComponent(entity, new TestPosition { X = 1 });

        await Assert.That(() =>
        {
            var pos = new TestPosition { X = 2 };
            var data = MemoryMarshal.AsBytes(new ReadOnlySpan<TestPosition>(in pos));
            _world.AddComponentRaw(entity, TestPosition.TypeId, data);
        }).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task RemoveComponentRaw_RemovesComponent()
    {
        var entity = _world.Spawn();
        _world.AddComponent(entity, new TestPosition { X = 10 });

        _world.RemoveComponentRaw(entity, TestPosition.TypeId);

        await Assert.That(_world.HasComponent<TestPosition>(entity)).IsFalse();
        await Assert.That(_world.IsAlive(entity)).IsTrue();
    }

    [Test]
    public async Task RemoveComponentRaw_MissingComponent_Throws()
    {
        var entity = _world.Spawn();

        await Assert.That(() => _world.RemoveComponentRaw(entity, TestPosition.TypeId))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task SetComponentRaw_UpdatesValue()
    {
        var entity = _world.Spawn();
        _world.AddComponent(entity, new TestPosition { X = 1, Y = 2, Z = 3 });

        var newPos = new TestPosition { X = 10, Y = 20, Z = 30 };
        var data = MemoryMarshal.AsBytes(new ReadOnlySpan<TestPosition>(in newPos));
        _world.SetComponentRaw(entity, TestPosition.TypeId, data);

        var result = _world.GetComponent<TestPosition>(entity);
        await Assert.That(result.X).IsEqualTo(10f);
        await Assert.That(result.Y).IsEqualTo(20f);
        await Assert.That(result.Z).IsEqualTo(30f);
    }

    [Test]
    public async Task SetComponentRaw_MissingComponent_Throws()
    {
        var entity = _world.Spawn();

        await Assert.That(() =>
        {
            var pos = new TestPosition { X = 1 };
            var data = MemoryMarshal.AsBytes(new ReadOnlySpan<TestPosition>(in pos));
            _world.SetComponentRaw(entity, TestPosition.TypeId, data);
        }).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task SetComponentRaw_TagComponent_IsNoOp()
    {
        var entity = _world.Spawn();
        _world.AddComponent<TestTag>(entity);

        _world.SetComponentRaw(entity, TestTag.TypeId, ReadOnlySpan<byte>.Empty);

        await Assert.That(_world.HasComponent<TestTag>(entity)).IsTrue();
    }

    [Test]
    public async Task AddComponentRaw_PreservesExistingComponents()
    {
        var entity = _world.Spawn();
        _world.AddComponent(entity, new TestPosition { X = 10, Y = 20, Z = 30 });

        var vel = new TestVelocity { X = 1 };
        var data = MemoryMarshal.AsBytes(new ReadOnlySpan<TestVelocity>(in vel));
        _world.AddComponentRaw(entity, TestVelocity.TypeId, data);

        await Assert.That(_world.HasComponent<TestPosition>(entity)).IsTrue();
        await Assert.That(_world.HasComponent<TestVelocity>(entity)).IsTrue();
        var pos = _world.GetComponent<TestPosition>(entity);
        await Assert.That(pos.X).IsEqualTo(10f);
        await Assert.That(pos.Y).IsEqualTo(20f);
        await Assert.That(pos.Z).IsEqualTo(30f);
    }

    [Test]
    public async Task Spawn_ManyDeferredEntities_Works()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        const int count = 200;
        for (int i = 0; i < count; i++)
            ecb.Spawn();

        int countBefore = _world.EntityCount;
        ecb.Playback(_world);

        await Assert.That(_world.EntityCount).IsEqualTo(countBefore + count);
    }

    [Test]
    public async Task AddComponentRaw_WrongDataSize_Throws()
    {
        var entity = _world.Spawn();
        var data = new byte[1]; // Wrong size for TestPosition (12 bytes)

        await Assert.That(() => _world.AddComponentRaw(entity, TestPosition.TypeId, data))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task SetComponentRaw_WrongDataSize_Throws()
    {
        var entity = _world.Spawn();
        _world.AddComponent(entity, new TestPosition { X = 1 });
        var data = new byte[1]; // Wrong size for TestPosition (12 bytes)

        await Assert.That(() => _world.SetComponentRaw(entity, TestPosition.TypeId, data))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task Playback_CalledTwice_Throws()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        ecb.Spawn();
        ecb.Playback(_world);

        await Assert.That(() => ecb.Playback(_world)).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Dispose_WithoutPlayback_ReleasesReservedIds()
    {
        int idBefore;
        {
            var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
            var entity = ecb.Spawn();
            idBefore = entity.Id;
            // Dispose without Playback — should release the reserved ID
            ecb.Dispose();
        }

        // The released ID should be reused by the next reservation
        using var ecb2 = new EntityCommandBuffer(_world.EntityIdAllocator);
        var reused = ecb2.Spawn();
        await Assert.That(reused.Id).IsEqualTo(idBefore);
    }

    [Test]
    public async Task Clear_WithoutPlayback_ReleasesReservedIds()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        var entity = ecb.Spawn();
        int idBefore = entity.Id;

        ecb.Clear();

        // The released ID should be reused by the next reservation
        var reused = ecb.Spawn();
        await Assert.That(reused.Id).IsEqualTo(idBefore);
    }

    [Test]
    public async Task Clear_AfterPlayback_DoesNotReleaseIds()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        var entity = ecb.Spawn();
        int playedId = entity.Id;
        ecb.Playback(_world);

        ecb.Clear();

        // The played-back entity should still be alive
        await Assert.That(_world.IsAlive(entity)).IsTrue();

        // Next spawn should get a new ID, not the played-back one
        var next = ecb.Spawn();
        await Assert.That(next.Id).IsNotEqualTo(playedId);
    }

    [Test]
    public async Task Spawn_EntityNotAliveBeforePlayback()
    {
        using var ecb = new EntityCommandBuffer(_world.EntityIdAllocator);
        var entity = ecb.Spawn();

        // Entity has a real ID but is not alive until Playback
        await Assert.That(_world.IsAlive(entity)).IsFalse();

        ecb.Playback(_world);

        await Assert.That(_world.IsAlive(entity)).IsTrue();
    }
}
