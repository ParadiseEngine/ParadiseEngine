namespace Paradise.ECS.Jobs.Test;

/// <summary>Tests for <see cref="JobWaveScheduler"/> — system scheduling with persistent worker pool.</summary>
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
        var e1 = _world.Spawn();
        _world.AddComponent(e1, new TestPosition { X = 10, Y = 20, Z = 0 });
        _world.AddComponent(e1, new TestVelocity { X = 1, Y = 2, Z = 0 });

        var seqSchedule = SystemSchedule.Create()
            .Add<TestMovementSystem>()
            .Add<TestGravitySystem>()
            .Build<SequentialWaveScheduler>();

        seqSchedule.Run(_world);
        var seqPos = _world.GetComponent<TestPosition>(e1);
        var seqVel = _world.GetComponent<TestVelocity>(e1);

        _world.Clear();
        var e2 = _world.Spawn();
        _world.AddComponent(e2, new TestPosition { X = 10, Y = 20, Z = 0 });
        _world.AddComponent(e2, new TestVelocity { X = 1, Y = 2, Z = 0 });

        using var pool = new JobWorkerPool(2);
        var jobSchedule = SystemSchedule.Create()
            .Add<TestMovementSystem>()
            .Add<TestGravitySystem>()
            .Build(new JobWaveScheduler(pool));

        jobSchedule.Run(_world);
        var jobPos = _world.GetComponent<TestPosition>(e2);
        var jobVel = _world.GetComponent<TestVelocity>(e2);

        await Assert.That(jobPos.X).IsEqualTo(seqPos.X);
        await Assert.That(jobPos.Y).IsEqualTo(seqPos.Y);
        await Assert.That(jobPos.Z).IsEqualTo(seqPos.Z);
        await Assert.That(jobVel.X).IsEqualTo(seqVel.X);
        await Assert.That(jobVel.Y).IsEqualTo(seqVel.Y);
        await Assert.That(jobVel.Z).IsEqualTo(seqVel.Z);
    }

    [Test]
    public async Task Schedule_RunJobScheduler_UpdatesEveryEntityOncePerFrame()
    {
        using var pool = new JobWorkerPool(4);
        const int entityCount = 200;
        const int frameCount = 10;

        var entities = new Entity[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            entities[i] = _world.Spawn();
            _world.AddComponent(entities[i], new TestPosition { X = i, Y = i * 2, Z = 0 });
            _world.AddComponent(entities[i], new TestVelocity { X = 1, Y = 1, Z = 0 });
        }

        var schedule = SystemSchedule.Create()
            .Add<TestMovementSystem>()
            .Add<TestGravitySystem>()
            .Build(new JobWaveScheduler(pool));

        for (int frame = 0; frame < frameCount; frame++)
            schedule.Run(_world);

        for (int i = 0; i < entityCount; i++)
        {
            var pos = _world.GetComponent<TestPosition>(entities[i]);
            var velocity = _world.GetComponent<TestVelocity>(entities[i]);
            await Assert.That(pos.X).IsEqualTo((float)(i + frameCount));
            await Assert.That(pos.Z).IsEqualTo(0f);
            await Assert.That(velocity.X).IsEqualTo(1f);
            await Assert.That(velocity.Y).IsEqualTo((float)(1 << frameCount));
            await Assert.That(velocity.Z).IsEqualTo(0f);
        }
    }
}
