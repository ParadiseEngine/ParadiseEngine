using System.Text;

namespace Paradise.ECS.Test;

// ============================================================================
// Deterministic ECB playback test systems.
// Wave 1: three independent read-only systems recording spawns / despawns /
// component sets. Wave 2 (via [After<>]): a conflicting SetComponent writer and
// a structural RemoveComponent system. All structural work goes through the
// per-work-item ECB, so any wave scheduler must produce an identical world.
// ============================================================================

/// <summary>Wave 1: spawns one entity per Health holder through a placeholder chain
/// (Spawn → AddComponent → SetComponent → tag), exercising in-buffer remapping.</summary>
public ref partial struct DetSpawnChainSystem : IEntitySystem
{
    public ref readonly TestHealth Health;
    public EntityCommandBuffer Commands;

    public void Execute()
    {
        var spawned = Commands.Spawn();
        Commands.AddComponent(spawned, new TestPosition { X = Health.Current });
        Commands.SetComponent(spawned, new TestPosition { X = Health.Current + 1000, Y = Health.Max });
        Commands.AddComponent<TestTag>(spawned);
    }
}

/// <summary>Wave 1: despawns entities whose health dropped to zero or below.</summary>
public ref partial struct DetDespawnDeadSystem : IEntitySystem
{
    public Entity Entity;
    public ref readonly TestHealth Health;
    public EntityCommandBuffer Commands;

    public void Execute()
    {
        if (Health.Current <= 0)
            Commands.Despawn(Entity);
    }
}

/// <summary>Wave 1: first writer in the cross-wave SetComponent conflict (Amount = 1).
/// Requires TestDamage in the query so SetComponent always targets an existing component
/// (also keeps this system inert for unrelated <c>AddAll()</c> schedules).</summary>
public ref partial struct DetFirstDamageWriterSystem : IEntitySystem
{
    public Entity Entity;
    public ref readonly TestVelocity Velocity;
    public ref readonly TestDamage Damage;
    public EntityCommandBuffer Commands;

    public void Execute() => Commands.SetComponent(Entity, new TestDamage { Amount = 1 });
}

/// <summary>Wave 2: second writer in the cross-wave SetComponent conflict (Amount = 2).
/// Playback happens in schedule order, so the later wave must win deterministically.</summary>
[After<DetFirstDamageWriterSystem>]
public ref partial struct DetSecondDamageWriterSystem : IEntitySystem
{
    public Entity Entity;
    public ref readonly TestVelocity Velocity;
    public ref readonly TestDamage Damage;
    public EntityCommandBuffer Commands;

    public void Execute() => Commands.SetComponent(Entity, new TestDamage { Amount = 2 });
}

/// <summary>Wave 2: structurally removes Velocity from odd-id entities via the ECB.</summary>
[After<DetSpawnChainSystem>]
public ref partial struct DetRemoveVelocitySystem : IEntitySystem
{
    public Entity Entity;
    public ref readonly TestVelocity Velocity;
    public ref readonly TestDamage Damage;
    public EntityCommandBuffer Commands;

    public void Execute()
    {
        if ((Entity.Id & 1) == 1)
            Commands.RemoveComponent<TestVelocity>(Entity);
    }
}

// ============================================================================
// Tests
// ============================================================================

/// <summary>
/// The point of deterministic ECB playback: the SAME scenario run under
/// <see cref="SequentialWaveScheduler"/> and <see cref="ParallelWaveScheduler"/> must produce
/// EQUIVALENT worlds — including entity IDs — because buffers are per work item (not per
/// thread), played back in schedule order, and Spawn defers real ID allocation to playback.
/// </summary>
public sealed class DeterministicPlaybackTests
{
    private const int SeedEntityCount = 240;
    private const int RunsPerScenario = 2;
    private const int ParallelIterations = 15;

    /// <summary>
    /// Seeds a deterministic mix of archetypes:
    /// group A (i%3==0): Health only (dead when i%11==0 — despawn targets);
    /// group B (i%3==1): Velocity + Damage (conflict-writer targets);
    /// group C (i%3==2): Health + Velocity + Damage (hit by every wave-1 system);
    /// plus TestTag on every 5th entity to multiply archetypes/chunks (more work items).
    /// </summary>
    private static void Seed(World world)
    {
        for (int i = 0; i < SeedEntityCount; i++)
        {
            var e = world.Spawn();
            switch (i % 3)
            {
                case 0:
                    world.AddComponent(e, new TestHealth { Current = i % 11 == 0 ? 0 : i, Max = 100 + i });
                    break;
                case 1:
                    world.AddComponent(e, new TestVelocity { X = i, Y = 2 * i, Z = 0 });
                    world.AddComponent(e, new TestDamage { Amount = -1 });
                    break;
                default:
                    world.AddComponent(e, new TestHealth { Current = i, Max = 200 + i });
                    world.AddComponent(e, new TestVelocity { X = -i, Y = 0, Z = i });
                    world.AddComponent(e, new TestDamage { Amount = -2 });
                    break;
            }

            if (i % 5 == 0)
                world.AddComponent<TestTag>(e);
        }
    }

    /// <summary>Runs the full scenario on a fresh world and returns its final snapshot.</summary>
    private static string RunScenario(IWaveScheduler scheduler)
    {
        using var sharedWorld = SharedWorldFactory.Create();
        var world = sharedWorld.CreateWorld();
        Seed(world);

        using var schedule = SystemSchedule.Create(world)
            .Add<DetSpawnChainSystem>()
            .Add<DetDespawnDeadSystem>()
            .Add<DetFirstDamageWriterSystem>()
            .Add<DetSecondDamageWriterSystem>()
            .Add<DetRemoveVelocitySystem>()
            .Build(scheduler);

        for (int run = 0; run < RunsPerScenario; run++)
            schedule.Run();

        return Snapshot(world);
    }

    /// <summary>
    /// Captures every alive entity — ID, version, and all component values — into a
    /// deterministic string, so two worlds compare equal only if they match entity-for-entity.
    /// </summary>
    private static string Snapshot(World world)
    {
        var sb = new StringBuilder();
        var manager = world.EntityManager;
        for (int id = 0; id < manager.Capacity; id++)
        {
            var location = manager.GetLocation(id);
            if (location.Version == 0 || location.ArchetypeId < 0)
                continue; // Never used, or destroyed

            var entity = new Entity(id, location.Version);
            sb.Append(id).Append(':').Append(location.Version);

            if (world.HasComponent<TestPosition>(entity))
            {
                var p = world.GetComponent<TestPosition>(entity);
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"|P({p.X},{p.Y},{p.Z})");
            }
            if (world.HasComponent<TestVelocity>(entity))
            {
                var v = world.GetComponent<TestVelocity>(entity);
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"|V({v.X},{v.Y},{v.Z})");
            }
            if (world.HasComponent<TestHealth>(entity))
            {
                var h = world.GetComponent<TestHealth>(entity);
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"|H({h.Current},{h.Max})");
            }
            if (world.HasComponent<TestDamage>(entity))
            {
                var d = world.GetComponent<TestDamage>(entity);
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"|D({d.Amount})");
            }
            if (world.HasComponent<TestTag>(entity))
                sb.Append("|T");

            sb.AppendLine();
        }
        return sb.ToString();
    }

    [Test]
    public async Task SequentialAndParallelSchedulers_ProduceIdenticalWorlds_IncludingEntityIds()
    {
        string expected = RunScenario(new SequentialWaveScheduler());

        // Sanity: the scenario actually spawned entities and despawned the dead ones
        await Assert.That(expected.Length).IsGreaterThan(0);

        // Sequential is itself reproducible
        await Assert.That(RunScenario(new SequentialWaveScheduler())).IsEqualTo(expected);

        // Parallel matches sequential exactly, across many iterations to shake out interleavings
        for (int i = 0; i < ParallelIterations; i++)
        {
            string actual = RunScenario(new ParallelWaveScheduler());
            await Assert.That(actual).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task ConflictingSetComponent_AcrossWaves_LaterWaveWinsDeterministically()
    {
        using var sharedWorld = SharedWorldFactory.Create();
        var world = sharedWorld.CreateWorld();

        // Even-id entity so DetRemoveVelocitySystem leaves it alone
        var first = world.Spawn(); // id 0
        world.AddComponent(first, new TestVelocity { X = 1 });
        world.AddComponent(first, new TestDamage { Amount = -1 });

        foreach (IWaveScheduler scheduler in new IWaveScheduler[] { new SequentialWaveScheduler(), new ParallelWaveScheduler() })
        {
            using var schedule = SystemSchedule.Create(world)
                .Add<DetFirstDamageWriterSystem>()
                .Add<DetSecondDamageWriterSystem>()
                .Build(scheduler);

            schedule.Run();

            // Wave 1 wrote Amount=1, wave 2 wrote Amount=2; playback in schedule order → 2 wins
            await Assert.That(world.GetComponent<TestDamage>(first).Amount).IsEqualTo(2);
        }
    }

    [Test]
    public async Task PlaceholderChain_ThroughSchedule_SpawnsFullyInitializedEntities()
    {
        using var sharedWorld = SharedWorldFactory.Create();
        var world = sharedWorld.CreateWorld();

        var seed = world.Spawn();
        world.AddComponent(seed, new TestHealth { Current = 7, Max = 9 });

        using var schedule = SystemSchedule.Create(world)
            .Add<DetSpawnChainSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        // The spawned entity took the next fresh ID at playback time
        var spawned = world.World.GetEntity(seed.Id + 1);
        await Assert.That(world.IsAlive(spawned)).IsTrue();
        var pos = world.GetComponent<TestPosition>(spawned);
        await Assert.That(pos.X).IsEqualTo(1007f); // SetComponent through the placeholder won
        await Assert.That(pos.Y).IsEqualTo(9f);
        await Assert.That(world.HasComponent<TestTag>(spawned)).IsTrue();
    }
}
