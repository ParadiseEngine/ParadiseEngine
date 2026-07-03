namespace Paradise.ECS;

/// <summary>
/// Shared chunk-pairing rule for snapshot-read execution: a write-world chunk is paired with the
/// read world's chunk at the same (archetype id, chunk index) — stable across
/// <c>World.CopyFrom</c> because chunks are copied in index order with shared archetype IDs
/// (never pair by <see cref="ChunkHandle"/>: handle values differ between worlds). A chunk with
/// no read-world counterpart (entity spawned after the copy) falls back to reading itself.
/// Used by both <see cref="SystemSchedule{TMask,TConfig}"/> and generated world-system bodies.
/// </summary>
public static class SnapshotChunkPairing
{
    public static void Resolve<TMask, TConfig>(
        IWorld<TMask, TConfig> writeWorld,
        IWorld<TMask, TConfig>? readWorld,
        int archetypeId,
        int chunkIndex,
        ChunkHandle writeChunk,
        int writeEntityCount,
        out ChunkManager readChunkManager,
        out ChunkHandle readChunk)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
    {
        readChunkManager = writeWorld.ChunkManager;
        readChunk = writeChunk;
        if (readWorld is null) return;

        var readArchetype = readWorld.ArchetypeRegistry.GetById(archetypeId);
        if (readArchetype is null || chunkIndex >= readArchetype.ChunkCount) return;

        int perChunk = readArchetype.Layout.EntitiesPerChunk;
        int readEntityCount = Math.Min(perChunk, readArchetype.EntityCount - chunkIndex * perChunk);
        System.Diagnostics.Debug.Assert(
            writeEntityCount <= readEntityCount,
            "Read world diverged structurally from the write world — structural changes between CopyFrom and the schedule run violate the snapshot contract.");

        readChunkManager = readWorld.ChunkManager;
        readChunk = readArchetype.GetChunk(chunkIndex);
    }
}
