namespace Paradise.ECS.Test;

/// <summary>
/// Regression tests for concurrent lazy query resolution (issue: singleton-queryable and
/// world-system queries resolve at DISPATCH time on parallel scheduler threads; the query-id
/// dictionary and per-world query cache corrupted under that race — observed in the field as
/// "Operations that change non-concurrent collections must have exclusive access" from
/// <c>SharedArchetypeMetadata.GetOrCreateQueryId</c> on a game's first tick).
/// Pre-fix, these tests fail readily with collection-corruption exceptions.
/// </summary>
public sealed class ConcurrentQueryResolutionTests
{
    private static HashedKey<ImmutableQueryDescription<SmallBitSet<ulong>>> Description(int variant)
    {
        // Distinct descriptions per variant so the create path (not just the lookup path) is
        // exercised concurrently; three shapes cycle to also produce repeated lookups.
        var builder = new QueryBuilder<SmallBitSet<ulong>>();
        var description = (variant % 3) switch
        {
            0 => builder.With<TestPosition>().Description,
            1 => builder.With<TestVelocity>().Description,
            _ => builder.With<TestPosition>().With<TestVelocity>().Description,
        };
        return (HashedKey<ImmutableQueryDescription<SmallBitSet<ulong>>>)description;
    }

    [Test]
    public async Task GetOrCreateQueryId_UnderParallelFirstResolution_IsConsistent()
    {
        using var metadata = new SharedArchetypeMetadata<SmallBitSet<ulong>, DefaultConfig>(
            ComponentRegistry.Shared.TypeInfos, new DefaultConfig());

        const int threads = 8;
        const int perThread = 2_000;
        var ids = new int[threads * perThread];

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                ids[t * perThread + i] = metadata.GetOrCreateQueryId(Description(i));
            }
        });

        // Same description must have resolved to one stable id on every thread.
        for (var i = 0; i < ids.Length; i++)
        {
            await Assert.That(ids[i]).IsEqualTo(ids[i % perThread]);
        }
        await Assert.That(metadata.QueryDescriptionCount).IsEqualTo(3);
    }

    [Test]
    public async Task RegistryGetOrCreateQuery_UnderParallelFirstResolution_DoesNotCorrupt()
    {
        var config = new DefaultConfig();
        using var chunkManager = ChunkManager.Create(config);
        using var metadata = new SharedArchetypeMetadata<SmallBitSet<ulong>, DefaultConfig>(
            ComponentRegistry.Shared.TypeInfos, config);
        var registry = new ArchetypeRegistry<SmallBitSet<ulong>, DefaultConfig>(metadata, chunkManager);

        // Seed one archetype so query resolution has a real match to link.
        var positionMask = SmallBitSet<ulong>.Empty.Set(TestPosition.TypeId);
        registry.GetOrCreate((HashedKey<SmallBitSet<ulong>>)positionMask);

        const int iterations = 200;
        for (var run = 0; run < iterations; run++)
        {
            Parallel.For(0, 8, _ =>
            {
                for (var i = 0; i < 30; i++)
                {
                    var query = registry.GetOrCreateQuery(Description(i));
                    // Touch the result so the returned list is actually consumed.
                    foreach (var chunk in query.Chunks)
                    {
                        _ = chunk.EntityCount;
                    }
                }
            });
        }

        // The position query must have linked the seeded archetype exactly once.
        var positions = registry.GetOrCreateQuery(Description(0));
        var archetypeCount = 0;
        foreach (var archetype in positions.Archetypes)
        {
            archetypeCount++;
        }
        await Assert.That(archetypeCount).IsEqualTo(1);
    }
}
