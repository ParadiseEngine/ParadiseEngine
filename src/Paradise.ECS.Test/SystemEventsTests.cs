namespace Paradise.ECS.Test;

/// <summary>
/// Stage-1 runtime tests for the deferred event primitive (<see cref="WorldEventStore"/>,
/// <see cref="SystemEventWriter"/>): schedule-order merge determinism, cross-frame expiry,
/// snapshot round-trip via <see cref="World{TMask,TConfig}.CopyFrom"/>, and multi-type / fan-out
/// delivery. The schedule + generator wiring is covered in later stages. See docs/system-events.md.
/// </summary>
public sealed class SystemEventsTests
{
    private struct Died
    {
        public int NpcId;
    }

    private struct Broke
    {
        public int NpcId;
        public int Realm;
    }

    private static SystemEventWriter Writer(params Died[] events)
    {
        var w = new SystemEventWriter();
        foreach (var e in events)
            w.Append(e);
        return w;
    }

    [Test]
    public async Task Commit_MergesWritersInScheduleOrder()
    {
        var store = new WorldEventStore();
        var writers = new[] { Writer(new Died { NpcId = 1 }, new Died { NpcId = 3 }), Writer(new Died { NpcId = 2 }) };

        store.Commit(writers);

        var ids = ToIds(store.Incoming<Died>());
        var expected = new[] { 1, 3, 2 }; // writer 0's stream in order, then writer 1's
        await Assert.That(ids).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Commit_OrderIndependentOfFillThread()
    {
        // Each writer is filled by its own thread (as under a parallel wave), but committed in a
        // fixed order — the merged result must be identical every run, threading notwithstanding.
        static int[] Run()
        {
            var writers = new SystemEventWriter[8];
            System.Threading.Tasks.Parallel.For(0, writers.Length, i =>
            {
                var w = new SystemEventWriter();
                w.Append(new Died { NpcId = i * 10 });
                w.Append(new Died { NpcId = i * 10 + 1 });
                writers[i] = w;
            });
            var store = new WorldEventStore();
            store.Commit(writers);
            return ToIds(store.Incoming<Died>());
        }

        var a = Run();
        var b = Run();
        await Assert.That(a).IsEquivalentTo(b);
        await Assert.That(a[0]).IsEqualTo(0);   // writer 0 first
        await Assert.That(a[^1]).IsEqualTo(71); // writer 7 last
    }

    [Test]
    public async Task Incoming_ExpiresAfterAFrameWithNoEvents()
    {
        var store = new WorldEventStore();
        var writers = new[] { Writer(new Died { NpcId = 5 }) };
        store.Commit(writers);
        await Assert.That(store.Incoming<Died>().Length).IsEqualTo(1);

        store.Commit(ReadOnlySpan<SystemEventWriter>.Empty); // next frame, nothing produced
        await Assert.That(store.Incoming<Died>().Length).IsEqualTo(0);
    }

    [Test]
    public async Task Commit_DeliversMultipleEventTypes()
    {
        var store = new WorldEventStore();
        var w = new SystemEventWriter();
        w.Append(new Died { NpcId = 1 });
        w.Append(new Broke { NpcId = 2, Realm = 3 });
        w.Append(new Died { NpcId = 4 });
        var writers = new[] { w };
        store.Commit(writers);

        var expectedDied = new[] { 1, 4 };
        await Assert.That(ToIds(store.Incoming<Died>())).IsEquivalentTo(expectedDied);
        var broke = store.Incoming<Broke>().ToArray();
        await Assert.That(broke.Length).IsEqualTo(1);
        await Assert.That(broke[0].Realm).IsEqualTo(3);
    }

    [Test]
    public async Task CopyFrom_RoundTripsIncomingEvents()
    {
        var a = new WorldEventStore();
        var writers = new[] { Writer(new Died { NpcId = 7 }, new Died { NpcId = 8 }) };
        a.Commit(writers);

        var b = new WorldEventStore();
        b.CopyFrom(a);

        var expected = new[] { 7, 8 };
        await Assert.That(ToIds(b.Incoming<Died>())).IsEquivalentTo(expected);
    }

    [Test]
    public async Task WorldCopyFrom_CarriesEventsIntoTheSnapshot()
    {
        using var shared = new SharedWorld<SmallBitSet<ulong>, DefaultConfig>(ComponentRegistry.Shared.TypeInfos);
        var write = shared.CreateWorld();
        var snapshot = shared.CreateWorld();

        // Produce an event in `write` (as a wave would), then publish a snapshot of it.
        var writers = new[] { Writer(new Died { NpcId = 42 }) };
        write.Events.Commit(writers);
        snapshot.CopyFrom(write);

        var expected = new[] { 42 };
        await Assert.That(ToIds(snapshot.Events.Incoming<Died>())).IsEquivalentTo(expected);
    }

    [Test]
    public async Task SetIncoming_SeedsEventsForReaders()
    {
        var store = new WorldEventStore();
        var restored = new[] { new Died { NpcId = 9 }, new Died { NpcId = 10 } };
        store.SetIncoming<Died>(restored);

        var expected = new[] { 9, 10 };
        await Assert.That(ToIds(store.Incoming<Died>())).IsEquivalentTo(expected);
    }

    [Test]
    public async Task SetIncoming_ThenCopyFrom_RoundTrips()
    {
        var a = new WorldEventStore();
        var seed = new[] { new Died { NpcId = 3 } };
        a.SetIncoming<Died>(seed);

        var b = new WorldEventStore();
        b.CopyFrom(a);

        var expected = new[] { 3 };
        await Assert.That(ToIds(b.Incoming<Died>())).IsEquivalentTo(expected);
    }

    private static int[] ToIds(ReadOnlySpan<Died> span)
    {
        var ids = new int[span.Length];
        for (int i = 0; i < span.Length; i++)
            ids[i] = span[i].NpcId;
        return ids;
    }
}
