using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;

namespace Paradise.ECS.Concurrent.ConcurrentTest;

/// <summary>Systematic tests of <see cref="EntityIdAllocator"/>'s monitor-guarded free list.</summary>
public static class EntityIdAllocatorTests
{
    /// <summary>Threads reserving and releasing concurrently never hold one ID at the same time.</summary>
    [Test]
    public static void ConcurrentReserveRelease_NeverHandsOneIdToTwoHolders()
    {
        var allocator = new EntityIdAllocator();
        const int threadCount = 4;
        const int rounds = 6;
        // Each thread holds at most one ID at a time, so no more fresh IDs than threads are needed.
        var held = new int[threadCount];

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int r = 0; r < rounds; r++)
                {
                    var entity = allocator.Reserve();
                    Specification.Assert(entity.Id < threadCount, $"Fresh ID {entity.Id} reserved while {threadCount} suffice.");
                    Specification.Assert(Interlocked.Exchange(ref held[entity.Id], 1) == 0,
                        $"Entity ID {entity.Id} reserved while another thread held it.");
                    Volatile.Write(ref held[entity.Id], 0);
                    allocator.Release(entity.Id, entity.Version + 1);
                }
            });
        }

        Task.WaitAll(tasks);

        Specification.Assert(allocator.PeekNextFreshId() <= threadCount,
            $"Expected at most {threadCount} fresh IDs, got {allocator.PeekNextFreshId()}.");
    }

    /// <summary>Releases racing each other, through the free list's growth, lose no slot and keep each version.</summary>
    [Test]
    public static void ConcurrentRelease_KeepsEverySlotAndVersion()
    {
        var allocator = new EntityIdAllocator();
        const int threadCount = 4;
        const int perThread = 5; // 20 releases grow the 16-slot list under contention

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int first = t * perThread;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                    allocator.Release(first + i, (uint)(first + i + 2));
            });
        }

        Task.WaitAll(tasks);

        var seen = new bool[threadCount * perThread];
        for (int i = 0; i < seen.Length; i++)
        {
            var entity = allocator.Reserve();
            Specification.Assert(entity.Id >= 0 && entity.Id < seen.Length && !seen[entity.Id],
                $"Reservation {i} returned ID {entity.Id}, missing or repeated.");
            Specification.Assert(entity.Version == (uint)(entity.Id + 2),
                $"ID {entity.Id} came back with version {entity.Version} instead of {entity.Id + 2}.");
            seen[entity.Id] = true;
        }
        Specification.Assert(allocator.PeekNextId() == allocator.PeekNextFreshId(), "Released slots remain after every one was reserved.");
    }

    /// <summary>Concurrent reservations drawing from free slots and fresh IDs at once never return one ID twice.</summary>
    [Test]
    public static void ConcurrentReserve_FreeAndFreshIdsStayDistinct()
    {
        var allocator = new EntityIdAllocator();
        const int freed = 3;
        for (int i = 0; i < freed; i++) allocator.Reserve();
        for (int i = 0; i < freed; i++) allocator.Release(i, 2);

        const int threadCount = 4;
        const int perThread = 2;
        var reserved = new Entity[threadCount * perThread];
        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int first = t * perThread;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                    reserved[first + i] = allocator.Reserve();
            });
        }

        Task.WaitAll(tasks);

        var ids = new HashSet<int>();
        foreach (var entity in reserved)
            Specification.Assert(ids.Add(entity.Id), $"Entity ID {entity.Id} was reserved twice.");
        // The freed IDs were fresh ones, so every reservation past them took the next fresh ID.
        Specification.Assert(allocator.PeekNextFreshId() == reserved.Length,
            $"Expected the next fresh ID to be {reserved.Length}, got {allocator.PeekNextFreshId()}.");
    }
}
