using System.Diagnostics;

namespace Paradise.ECS.Sample.Samples;

/// <summary>
/// Demonstrates entity handle injection in systems for targeted ECB operations.
/// Systems can declare an Entity field (entity systems) or ReadOnlySpan&lt;Entity&gt; (chunk systems)
/// to receive the current entity handle(s) being processed.
/// </summary>
public static class EntityInjectionSample
{
    public static void Run(World world)
    {
        Console.WriteLine("13. Entity Handle Injection in Systems");
        Console.WriteLine("----------------------------");

        // Clear world for isolation from previous samples
        world.Clear();

        // ---- Entity System: Despawn dead entities ----
        Console.WriteLine("  DespawnDeadSystem (entity system with Entity field):");

        var alive1 = world.Spawn();
        world.AddComponent(alive1, new Health(100));

        var alive2 = world.Spawn();
        world.AddComponent(alive2, new Health { Current = 50, Max = 100 });

        var dead1 = world.Spawn();
        world.AddComponent(dead1, new Health { Current = 0, Max = 100 });

        var dead2 = world.Spawn();
        world.AddComponent(dead2, new Health { Current = -10, Max = 100 });

        Debug.Assert(world.EntityCount == 4, $"Expected 4 entities before despawn, got {world.EntityCount}");
        Console.WriteLine($"    Before: {world.EntityCount} entities (2 healthy, 2 dead)");

        var despawnSchedule = SystemSchedule.Create(world)
            .Add<DespawnDeadSystem>()
            .Build<SequentialWaveScheduler>();

        despawnSchedule.Run();

        Debug.Assert(world.IsAlive(alive1), "alive1 should still be alive");
        Debug.Assert(world.IsAlive(alive2), "alive2 should still be alive");
        Debug.Assert(!world.IsAlive(dead1), "dead1 should be despawned");
        Debug.Assert(!world.IsAlive(dead2), "dead2 should be despawned");
        Debug.Assert(world.EntityCount == 2, $"Expected 2 entities after despawn, got {world.EntityCount}");

        Console.WriteLine($"    After:  {world.EntityCount} entities (dead entities despawned)");
        Console.WriteLine($"    alive1={world.IsAlive(alive1)}, alive2={world.IsAlive(alive2)}, dead1={world.IsAlive(dead1)}, dead2={world.IsAlive(dead2)}");

        world.Clear();

        // ---- Entity System: Spawn clone when low health ----
        Console.WriteLine("  SpawnCloneWhenLowHealthSystem (entity + position + health + ECB):");

        var hero = world.Spawn();
        world.AddComponent(hero, new Position(42, 99));
        world.AddComponent(hero, new Health { Current = 15, Max = 100 });  // Low health → triggers clone

        var healthy = world.Spawn();
        world.AddComponent(healthy, new Position(10, 20));
        world.AddComponent(healthy, new Health(100));  // Healthy → no clone

        Debug.Assert(world.EntityCount == 2, $"Expected 2 entities before clone, got {world.EntityCount}");

        var cloneSchedule = SystemSchedule.Create(world)
            .Add<SpawnCloneWhenLowHealthSystem>()
            .Build<SequentialWaveScheduler>();

        cloneSchedule.Run();

        Debug.Assert(world.EntityCount == 3, $"Expected 3 entities after clone (2 + 1 clone), got {world.EntityCount}");

        Console.WriteLine($"    Before: 2 entities, After: {world.EntityCount} entities (1 clone spawned for low-health hero)");

        world.Clear();

        // ---- Chunk System: Batch despawn dead entities ----
        Console.WriteLine("  ChunkDespawnDeadSystem (chunk system with ReadOnlySpan<Entity>):");

        var ca = world.Spawn();
        world.AddComponent(ca, new Health(80));

        var cb = world.Spawn();
        world.AddComponent(cb, new Health { Current = 0, Max = 50 });

        var cc = world.Spawn();
        world.AddComponent(cc, new Health(60));

        Debug.Assert(world.EntityCount == 3, $"Expected 3 entities before chunk despawn, got {world.EntityCount}");
        Console.WriteLine($"    Before: {world.EntityCount} entities (1 dead)");

        var chunkDespawnSchedule = SystemSchedule.Create(world)
            .Add<ChunkDespawnDeadSystem>()
            .Build<SequentialWaveScheduler>();

        chunkDespawnSchedule.Run();

        Debug.Assert(world.IsAlive(ca), "ca should still be alive");
        Debug.Assert(!world.IsAlive(cb), "cb should be despawned");
        Debug.Assert(world.IsAlive(cc), "cc should still be alive");
        Debug.Assert(world.EntityCount == 2, $"Expected 2 entities after chunk despawn, got {world.EntityCount}");

        Console.WriteLine($"    After:  {world.EntityCount} entities (dead entity despawned via chunk system)");

        world.Clear();

        Console.WriteLine();
    }
}
