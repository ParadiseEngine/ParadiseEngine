namespace Paradise.ECS.Jobs.Test;

/// <summary>
/// Tests for <see cref="JobWaveScheduler"/> — system scheduling with persistent worker pool.
/// </summary>
public sealed class JobWaveSchedulerTests : IDisposable
{
    private readonly SharedWorld _sharedWorld;
    private readonly World _world;

    public JobWaveSchedulerTests()
    {
        _sharedWorld = SharedWorldFactory.Create();
        _world = _sharedWorld.CreateWorld();
    }

    public void Dispose()
    {
        _sharedWorld.Dispose();
    }

    [Test]
    public async Task Schedule_RunJobScheduler_ProducesSameResultsAsSequential()
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

        // Reset and run with JobWaveScheduler
        _world.Clear();
        var e2 = _world.Spawn();
        _world.AddComponent(e2, new TestPosition { X = 10, Y = 20, Z = 0 });
        _world.AddComponent(e2, new TestVelocity { X = 1, Y = 2, Z = 0 });

        using var pool = new JobWorkerPool(2);
        var jobSchedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Add<TestGravitySystem>()
            .Build(new JobWaveScheduler(pool));

        jobSchedule.Run();
        var jobPos = _world.GetComponent<TestPosition>(e2);
        var jobVel = _world.GetComponent<TestVelocity>(e2);

        await Assert.That(jobPos.X).IsEqualTo(seqPos.X);
        await Assert.That(jobPos.Y).IsEqualTo(seqPos.Y);
        await Assert.That(jobVel.Y).IsEqualTo(seqVel.Y);
    }

    [Test]
    public async Task Schedule_RunJobScheduler_StressTestMultipleFrames()
    {
        using var pool = new JobWorkerPool(4);
        const int entityCount = 200;
        const int frameCount = 10;

        // Create many entities
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
            .Build(new JobWaveScheduler(pool));

        for (int frame = 0; frame < frameCount; frame++)
            schedule.Run();

        // Verify all entities were processed — positions should have increased
        for (int i = 0; i < entityCount; i++)
        {
            var pos = _world.GetComponent<TestPosition>(entities[i]);
            await Assert.That(pos.X).IsGreaterThan((float)i);
        }
    }
}
