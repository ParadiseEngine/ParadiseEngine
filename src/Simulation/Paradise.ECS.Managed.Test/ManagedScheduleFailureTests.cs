using System.Runtime.CompilerServices;

namespace Paradise.ECS.Managed.Test;

public sealed class ManagedScheduleFailureTests
{
    private static readonly ConditionalWeakTable<IWorld<ComponentMask, DefaultConfig>, RunState> s_runs = new();

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task FailedRun_ReleasesUnplayedManagedValuesAndCanReuseSchedule(bool playbackFailure, bool differentWorld)
    {
        using var shared = SharedWorldFactory.Create();
        var source = shared.CreateWorld();
        var state = new RunState { Fail = true, PlaybackFailure = playbackFailure, Value = 1 };
        s_runs.Add(source, state);
        using var schedule = SystemSchedule.Create()
            .AddWorld<FailingManagedSystem>()
            .Build<SequentialWaveScheduler>();

        await Assert.That(() => schedule.Run(source)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(source.EntityCount).IsEqualTo(playbackFailure ? 1 : 0);
        await Assert.That(source.Events.Incoming<int>().Length).IsEqualTo(0);
        Collect();
        await Assert.That(state.StagedReference!.IsAlive).IsFalse();

        var retry = differentWorld ? shared.CreateWorld() : source;
        if (differentWorld)
            s_runs.Add(retry, new RunState { Value = 2 });
        else
        {
            state.Fail = false;
            state.Value = 2;
        }

        int before = retry.EntityCount;
        schedule.Run(retry);
        await Assert.That(retry.EntityCount).IsEqualTo(before + 1);
        await Assert.That(retry.GetManaged<RuntimePayload>(retry.GetEntity(before))!.Value).IsEqualTo(2);
        await Assert.That(retry.Events.Incoming<int>().ToArray()).IsEquivalentTo([2]);

        schedule.Run(retry);
        await Assert.That(retry.EntityCount).IsEqualTo(before + 2);
        await Assert.That(retry.GetManaged<RuntimePayload>(retry.GetEntity(before + 1))!.Value).IsEqualTo(2);
        if (differentWorld)
            await Assert.That(source.EntityCount).IsEqualTo(playbackFailure ? 1 : 0);
    }

    private sealed class RunState
    {
        public bool Fail { get; set; }
        public bool PlaybackFailure { get; init; }
        public int Value { get; set; }
        public WeakReference? StagedReference { get; set; }
    }

    private readonly struct FailingManagedSystem : IWorldSystemRunner<ComponentMask, DefaultConfig>
    {
        public static int SystemId => 1_000_002;

        public static SystemMetadata<ComponentMask> Metadata => new()
        {
            SystemId = FailingManagedSystem.SystemId,
            TypeName = nameof(FailingManagedSystem),
        };

        public static void RunWorld(
            IWorld<ComponentMask, DefaultConfig> world,
            IWorld<ComponentMask, DefaultConfig>? readWorld,
            EntityCommandBuffer commands,
            SystemEventWriter eventWriter)
        {
            var state = s_runs.GetValue(world, static _ => new RunState());
            StageValue(commands, state);
            eventWriter.Append(state.Value);
            if (state.Fail && !state.PlaybackFailure)
                throw new InvalidOperationException("Managed system execution failed.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StageValue(EntityCommandBuffer commands, RunState state)
        {
            var entity = commands.Spawn();
            if (state.Fail && state.PlaybackFailure)
                commands.RecordExtension<UnsupportedOp>(entity);
            var value = new RuntimePayload { Value = state.Value };
            commands.AddManaged(entity, value);
            state.StagedReference = new WeakReference(value);
        }
    }

    private readonly struct UnsupportedOp : ICommandExtension;

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
