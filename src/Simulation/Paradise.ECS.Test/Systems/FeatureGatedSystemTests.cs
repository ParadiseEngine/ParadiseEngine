using Paradise.Features;

namespace Paradise.ECS.Test;

/// <summary>Flips a feature switch from INSIDE a run — the deterministic stand-in for a debug
/// panel or a config reload doing it on another thread, which is what makes the snapshot's
/// absence observable without a race.</summary>
public ref partial struct FlipsAFeatureMidRunSystem : IWorldSystem
{
    public static FeatureSwitches? Switches { get; set; }
    public static FeatureId Target { get; set; }

    public WsMovable.Segments Movable;

    public void Execute() => Switches?.Set(Target, false);
}

/// <summary>Ordered after the flipper, so it lands in a later wave and would be skipped by a
/// schedule that re-read the switch as it walked.</summary>
[After<FlipsAFeatureMidRunSystem>]
public ref partial struct MovesAfterTheFlipSystem : IEntitySystem
{
    public ref TestPosition Position;
    public ref readonly TestVelocity Velocity;

    public void Execute() =>
        Position = new TestPosition { X = Position.X + Velocity.X, Y = Position.Y, Z = Position.Z };
}

/// <summary>A gameplay feature is a set of systems, switched from the same engine configuration
/// the renderer reads. The schedule skips a gated system whose feature is off, and picks it up
/// again the run after it comes back — no rebuild, no <c>if</c> inside the system.</summary>
public sealed class FeatureGatedSystemTests : IDisposable
{
    private static readonly FeatureDefinition s_movement = new("gameplay.movement", true, "Entities move.");
    private static readonly FeatureDefinition s_gravity = new("gameplay.gravity", true, "Velocity accelerates.");

    private readonly SharedWorld _sharedWorld;
    private readonly World _world;

    public FeatureGatedSystemTests()
    {
        _sharedWorld = SharedWorldFactory.Create();
        _world = _sharedWorld.CreateWorld();
    }

    public void Dispose() => _sharedWorld.Dispose();

    private Entity SpawnMover()
    {
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 0, Y = 0, Z = 0 });
        _world.AddComponent(e, new TestVelocity { X = 1, Y = 1, Z = 0 });
        return e;
    }

    private static FeatureSwitches Switches()
    {
        var switches = new FeatureSwitches();
        switches.Declare(s_movement);
        switches.Declare(s_gravity);
        return switches;
    }

    [Test]
    public async Task a_gated_system_runs_while_its_feature_is_on()
    {
        var e = SpawnMover();
        var schedule = SystemSchedule.Create()
            .Add<TestMovementSystem>(s_movement.Id)
            .Build<SequentialWaveScheduler>(Switches());

        schedule.Run(_world);

        await Assert.That(_world.GetComponent<TestPosition>(e).X).IsEqualTo(1f);
    }

    [Test]
    public async Task a_gated_system_does_not_run_while_its_feature_is_off()
    {
        var e = SpawnMover();
        var switches = Switches();
        switches.Set(s_movement.Id, false);
        var schedule = SystemSchedule.Create()
            .Add<TestMovementSystem>(s_movement.Id)
            .Build<SequentialWaveScheduler>(switches);

        schedule.Run(_world);

        await Assert.That(_world.GetComponent<TestPosition>(e).X).IsEqualTo(0f);
    }

    /// <summary>The switch is read on every run, so a feature turned off between two ticks stops
    /// at the next one and starts again when it comes back.</summary>
    [Test]
    public async Task a_feature_switched_between_runs_takes_effect_on_the_next_run()
    {
        var e = SpawnMover();
        var switches = Switches();
        var schedule = SystemSchedule.Create()
            .Add<TestMovementSystem>(s_movement.Id)
            .Build<SequentialWaveScheduler>(switches);

        schedule.Run(_world);
        switches.Set(s_movement.Id, false);
        schedule.Run(_world);
        schedule.Run(_world);
        switches.Set(s_movement.Id, true);
        schedule.Run(_world);

        await Assert.That(_world.GetComponent<TestPosition>(e).X).IsEqualTo(2f);
    }

    /// <summary>A switch flipped WHILE a run is in progress lands on the NEXT run. Every system
    /// of a feature belongs to the same tick or none of it does.
    ///
    /// <para>Re-read as the waves are walked instead, a feature switched off midway runs the
    /// systems already scheduled and skips the rest — a tick in which a gameplay feature half
    /// happened, and which half depended on another thread's timing.</para></summary>
    [Test]
    public async Task a_feature_switched_off_mid_run_still_finishes_that_run()
    {
        var e = SpawnMover();
        var switches = Switches();
        FlipsAFeatureMidRunSystem.Switches = switches;
        FlipsAFeatureMidRunSystem.Target = s_movement.Id;
        try
        {
            var schedule = SystemSchedule.Create()
                .AddWorld<FlipsAFeatureMidRunSystem>()
                .Add<MovesAfterTheFlipSystem>(s_movement.Id)
                .Build<SequentialWaveScheduler>(switches);

            schedule.Run(_world);
            var afterTheRunThatFlipped = _world.GetComponent<TestPosition>(e).X;
            schedule.Run(_world);
            var afterTheNext = _world.GetComponent<TestPosition>(e).X;

            // The flip happened in wave 0; the gated system is in wave 1 and still ran.
            await Assert.That(afterTheRunThatFlipped).IsEqualTo(1f);
            await Assert.That(switches.IsEnabled(s_movement.Id)).IsFalse();
            // The next run is the one that adopts it.
            await Assert.That(afterTheNext).IsEqualTo(1f);
        }
        finally
        {
            FlipsAFeatureMidRunSystem.Switches = null;
        }
    }

    /// <summary>One feature off does not disturb the systems around it, including the ones the
    /// DAG ordered against it.</summary>
    [Test]
    public async Task the_ungated_systems_around_it_are_unaffected()
    {
        var e = SpawnMover();
        var switches = Switches();
        switches.Set(s_gravity.Id, false);
        var schedule = SystemSchedule.Create()
            .Add<TestGravitySystem>(s_gravity.Id)
            .Add<TestMovementSystem>(s_movement.Id)
            .Build<SequentialWaveScheduler>(switches);

        schedule.Run(_world);

        // Gravity would have doubled velocity.Y before movement read it.
        await Assert.That(_world.GetComponent<TestVelocity>(e).Y).IsEqualTo(1f);
        await Assert.That(_world.GetComponent<TestPosition>(e).Y).IsEqualTo(1f);
    }

    /// <summary>A system added without a feature has no gate, and a schedule built without a
    /// switchboard runs everything — so nothing that existed before this became possible
    /// changes behaviour.</summary>
    [Test]
    public async Task an_ungated_system_and_a_schedule_with_no_switchboard_both_always_run()
    {
        var e = SpawnMover();
        var switches = Switches();
        switches.Set(s_movement.Id, false);
        var mixed = SystemSchedule.Create()
            .Add<TestMovementSystem>()
            .Build<SequentialWaveScheduler>(switches);
        var unconfigured = SystemSchedule.Create()
            .Add<TestGravitySystem>(s_gravity.Id)
            .Build<SequentialWaveScheduler>();

        mixed.Run(_world);
        unconfigured.Run(_world);

        await Assert.That(_world.GetComponent<TestPosition>(e).X).IsEqualTo(1f);
        await Assert.That(_world.GetComponent<TestVelocity>(e).Y).IsEqualTo(2f);
    }

    /// <summary>A gated system that is off contributes no work items, so it also rents no command
    /// buffer — and the systems that do run keep the rent order that makes playback
    /// deterministic.</summary>
    [Test]
    public async Task a_parallel_scheduler_reaches_the_same_world_as_a_sequential_one()
    {
        var e = SpawnMover();
        var second = _world.Spawn();
        _world.AddComponent(second, new TestPosition { X = 5, Y = 5, Z = 0 });
        _world.AddComponent(second, new TestVelocity { X = 2, Y = 2, Z = 0 });
        var switches = Switches();
        switches.Set(s_gravity.Id, false);
        var schedule = SystemSchedule.Create()
            .Add<TestGravitySystem>(s_gravity.Id)
            .Add<TestMovementSystem>(s_movement.Id)
            .Build(new ParallelWaveScheduler(), switches);

        schedule.Run(_world);

        await Assert.That(_world.GetComponent<TestPosition>(e).X).IsEqualTo(1f);
        await Assert.That(_world.GetComponent<TestPosition>(second).X).IsEqualTo(7f);
    }
}
