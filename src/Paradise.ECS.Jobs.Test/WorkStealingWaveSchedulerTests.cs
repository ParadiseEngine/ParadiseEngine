namespace Paradise.ECS.Jobs.Test;

/// <summary>
/// Tests for <see cref="WorkStealingWaveScheduler"/> — system scheduling with work-stealing pool.
/// </summary>
public sealed class WorkStealingWaveSchedulerTests : IDisposable
{
    private readonly SharedWorld _sharedWorld;
    private readonly World _world;

    public WorkStealingWaveSchedulerTests()
    {
        _sharedWorld = SharedWorldFactory.Create();
        _world = _sharedWorld.CreateWorld();
    }

    public void Dispose()
    {
        _sharedWorld.Dispose();
    }

    [Test]
    public async Task Schedule_RunWorkStealingScheduler_ProducesSameResultsAsSequential()
    {
        // Run sequential
        var e1 = _world.Spawn();
        _world.AddComponent(e1, new TestPosition { X = 10, Y = 20, Z = 0 });
        _world.AddComponent(e1, new TestVelocity { X = 1, Y = 2, Z = 0 });

        var seqSchedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Add<TestGravitySystem>()
            .Build<SequentialWaveScheduler>();

        seqSchedule.Run();
        var seqPos = _world.GetComponent<TestPosition>(e1);
        var seqVel = _world.GetComponent<TestVelocity>(e1);

        // Reset and run with WorkStealingWaveScheduler
        _world.Clear();
        var e2 = _world.Spawn();
        _world.AddComponent(e2, new TestPosition { X = 10, Y = 20, Z = 0 });
        _world.AddComponent(e2, new TestVelocity { X = 1, Y = 2, Z = 0 });

        using var pool = new WorkStealingPool(2);
        var wsSchedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Add<TestGravitySystem>()
            .Build(new WorkStealingWaveScheduler(pool));

        wsSchedule.Run();
        var wsPos = _world.GetComponent<TestPosition>(e2);
        var wsVel = _world.GetComponent<TestVelocity>(e2);

        await Assert.That(wsPos.X).IsEqualTo(seqPos.X);
        await Assert.That(wsPos.Y).IsEqualTo(seqPos.Y);
        await Assert.That(wsVel.Y).IsEqualTo(seqVel.Y);
    }

    [Test]
    public async Task Schedule_RunWorkStealingScheduler_StressTestMultipleFrames()
    {
        using var pool = new WorkStealingPool(4);
        const int entityCount = 200;
        const int frameCount = 10;

        var entities = new Entity[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            entities[i] = _world.Spawn();
            _world.AddComponent(entities[i], new TestPosition { X = i, Y = i * 2, Z = 0 });
            _world.AddComponent(entities[i], new TestVelocity { X = 1, Y = 1, Z = 0 });
        }

        var schedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Add<TestGravitySystem>()
            .Build(new WorkStealingWaveScheduler(pool));

        for (int frame = 0; frame < frameCount; frame++)
            schedule.Run();

        for (int i = 0; i < entityCount; i++)
        {
            var pos = _world.GetComponent<TestPosition>(entities[i]);
            await Assert.That(pos.X).IsGreaterThan((float)i);
        }
    }

    [Test]
    public async Task Schedule_WorkStealingMatchesJobScheduler_SameResults()
    {
        const int entityCount = 50;

        // Run with JobWaveScheduler
        var entities1 = new Entity[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            entities1[i] = _world.Spawn();
            _world.AddComponent(entities1[i], new TestPosition { X = i, Y = i * 2, Z = 0 });
            _world.AddComponent(entities1[i], new TestVelocity { X = 1, Y = 1, Z = 0 });
        }

        using var jobPool = new JobWorkerPool(2);
        var jobSchedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Add<TestGravitySystem>()
            .Build(new JobWaveScheduler(jobPool));

        jobSchedule.Run();
        var jobPositions = new TestPosition[entityCount];
        for (int i = 0; i < entityCount; i++)
            jobPositions[i] = _world.GetComponent<TestPosition>(entities1[i]);

        // Reset and run with WorkStealingWaveScheduler
        _world.Clear();
        var entities2 = new Entity[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            entities2[i] = _world.Spawn();
            _world.AddComponent(entities2[i], new TestPosition { X = i, Y = i * 2, Z = 0 });
            _world.AddComponent(entities2[i], new TestVelocity { X = 1, Y = 1, Z = 0 });
        }

        using var wsPool = new WorkStealingPool(2);
        var wsSchedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Add<TestGravitySystem>()
            .Build(new WorkStealingWaveScheduler(wsPool));

        wsSchedule.Run();

        for (int i = 0; i < entityCount; i++)
        {
            var wsPos = _world.GetComponent<TestPosition>(entities2[i]);
            await Assert.That(wsPos.X).IsEqualTo(jobPositions[i].X);
            await Assert.That(wsPos.Y).IsEqualTo(jobPositions[i].Y);
        }
    }
}
