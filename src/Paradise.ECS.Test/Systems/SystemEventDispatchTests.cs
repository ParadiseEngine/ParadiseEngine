namespace Paradise.ECS.Test;

// ============================================================================
// Stage-2 tests: generator-injected SystemEventWriter / SystemEventReader driven
// through a real SystemSchedule. Proves writer injection + schedule-order merge +
// one-frame reader delivery, under both wave schedulers. See docs/system-events.md.
// ============================================================================

/// <summary>A test event type (plain unmanaged struct, not a component).</summary>
public struct Ping
{
    public int V;
}

/// <summary>Captures what a consumer system observed, for assertions.</summary>
internal static class PingSink
{
    public static readonly List<int> Seen = new();
}

/// <summary>World system that emits one event via the injected writer.</summary>
public ref partial struct PingProducerA : IWorldSystem
{
    public SystemEventWriter Writer;

    public void Execute() => Writer.Append(new Ping { V = 1 });
}

/// <summary>Second producer — used to prove schedule-order merge across systems.</summary>
public ref partial struct PingProducerB : IWorldSystem
{
    public SystemEventWriter Writer;

    public void Execute() => Writer.Append(new Ping { V = 2 });
}

/// <summary>World system that reads last frame's events via the injected reader.</summary>
public ref partial struct PingConsumer : IWorldSystem
{
    public SystemEventReader Reader;

    public void Execute()
    {
        PingSink.Seen.Clear();
        foreach (var p in Reader.Read<Ping>())
            PingSink.Seen.Add(p.V);
    }
}

/// <summary>Entity system that emits one event per entity — exercises the per-chunk writer path.</summary>
public ref partial struct PingEntityProducer : IEntitySystem
{
    public ref readonly TestPosition Pos;
    public SystemEventWriter Writer;

    public void Execute() => Writer.Append(new Ping { V = (int)Pos.X });
}

public sealed class SystemEventDispatchTests : IDisposable
{
    private readonly SharedWorld _sharedWorld;

    public SystemEventDispatchTests()
    {
        _sharedWorld = SharedWorldFactory.Create();
    }

    public void Dispose() => _sharedWorld.Dispose();

    [Test]
    public async Task Writer_injection_commits_events_in_schedule_order()
    {
        var world = _sharedWorld.CreateWorld();
        using var schedule = SystemSchedule.Create(world)
            .AddWorld<PingProducerA>()
            .AddWorld<PingProducerB>()
            .Build<SequentialWaveScheduler>();
        schedule.Run();

        var expected = new[] { 1, 2 };
        await Assert.That(Values(world.Events.Incoming<Ping>())).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Merge_order_is_identical_under_parallel_and_sequential_schedulers()
    {
        int[] RunUnder<TScheduler>() where TScheduler : IWaveScheduler, new()
        {
            var world = _sharedWorld.CreateWorld();
            for (int i = 0; i < 500; i++)
            {
                var e = world.Spawn();
                world.AddComponent(e, new TestPosition { X = i, Y = 0, Z = 0 });
            }
            using var schedule = SystemSchedule.Create(world)
                .Add<PingEntityProducer>()
                .Build<TScheduler>();
            schedule.Run();
            return Values(world.Events.Incoming<Ping>());
        }

        var sequential = RunUnder<SequentialWaveScheduler>();
        var parallel = RunUnder<ParallelWaveScheduler>();

        await Assert.That(sequential.Length).IsEqualTo(500);
        await Assert.That(parallel).IsEquivalentTo(sequential); // threading must not change order
    }

    [Test]
    public async Task Reader_observes_previous_frames_events_across_the_snapshot()
    {
        // Frame N: producer emits into `produced`.
        var produced = _sharedWorld.CreateWorld();
        using (var producerSchedule = SystemSchedule.Create(produced)
                   .AddWorld<PingProducerA>()
                   .AddWorld<PingProducerB>()
                   .Build<SequentialWaveScheduler>())
        {
            producerSchedule.Run();
        }

        // Frame N+1: a consumer whose read world is the previous frame's snapshot sees those events.
        var consumerWorld = _sharedWorld.CreateWorld();
        using (var consumerSchedule = SystemSchedule.Create(consumerWorld)
                   .AddWorld<PingConsumer>()
                   .Build<SequentialWaveScheduler>())
        {
            consumerSchedule.Run(readWorld: produced);
        }

        var expected = new[] { 1, 2 };
        await Assert.That(PingSink.Seen).IsEquivalentTo(expected);
    }

    private static int[] Values(ReadOnlySpan<Ping> span)
    {
        var v = new int[span.Length];
        for (int i = 0; i < span.Length; i++)
            v[i] = span[i].V;
        return v;
    }
}
