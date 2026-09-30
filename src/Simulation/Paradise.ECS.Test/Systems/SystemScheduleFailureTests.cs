using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TUnit.Assertions.Enums;

namespace Paradise.ECS.Test;

public sealed class SystemScheduleFailureTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SystemFailure_DiscardsStagingBeforeSameOrDifferentWorldReuse(bool parallel, bool differentWorld)
    {
        using var shared = new SharedWorld<SmallBitSet<ulong>, DefaultConfig>(ComponentRegistry.Shared.TypeInfos);
        var source = shared.CreateWorld();
        var target = Seed(source);
        source.Events.SetIncoming<int>([-1]);
        var retry = differentWorld ? shared.CreateWorld() : source;
        if (differentWorld)
            await Assert.That(Seed(retry)).IsEqualTo(target);

        var failure = new InvalidOperationException("System execution failed.");
        bool fail = true;
        var recordings = new Recording?[3];
        using var schedule = CreateSchedule(parallel,
            (world, readWorld, commands, events) => recordings[0] = Record(commands, events, fail ? 10 : 20),
            (world, readWorld, commands, events) =>
            {
                recordings[1] = Record(commands, events, fail ? 11 : 21);
                world.GetComponent<TestPosition>(target).X++;
                if (fail)
                    throw failure;
            },
            (world, readWorld, commands, events) => recordings[2] = Record(commands, events, fail ? 12 : 22));

        var caught = RunAndCatch(() => schedule.Run(source));
        await Assert.That(ReferenceEquals(Unwrap(caught), failure)).IsTrue();
        await Assert.That(source.EntityCount).IsEqualTo(1);
        await Assert.That(source.GetComponent<TestPosition>(target).X).IsEqualTo(1f);
        await Assert.That(source.Events.Incoming<int>().ToArray()).IsEquivalentTo([-1]);
        await AssertCleared(recordings).ConfigureAwait(false);

        // Failure releases the structural guard, but does not undo already-written component values.
        var outsideRun = source.Spawn();
        source.Despawn(outsideRun);
        fail = false;
        schedule.Run(retry);
        await Assert.That(retry.EntityCount).IsEqualTo(4);
        await Assert.That(retry.Events.Incoming<int>().ToArray())
            .IsEquivalentTo([20, 21, 22], CollectionOrdering.Matching);
        await AssertCleared(recordings).ConfigureAwait(false);

        // A second successful recording must also start with empty pooled buffers.
        schedule.Run(retry);
        await Assert.That(retry.EntityCount).IsEqualTo(7);
        await Assert.That(retry.Events.Incoming<int>().ToArray())
            .IsEquivalentTo([20, 21, 22], CollectionOrdering.Matching);
        if (differentWorld)
        {
            await Assert.That(source.EntityCount).IsEqualTo(1);
            await Assert.That(source.Events.Incoming<int>().ToArray()).IsEquivalentTo([-1]);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task PlaybackFailure_DiscardsPlayedFailingAndUnplayedBuffersBeforeReuse(bool parallel, bool differentWorld)
    {
        using var shared = new SharedWorld<SmallBitSet<ulong>, DefaultConfig>(ComponentRegistry.Shared.TypeInfos);
        var source = shared.CreateWorld();
        Seed(source);
        source.Events.SetIncoming<int>([-1]);
        var retry = differentWorld ? shared.CreateWorld() : source;
        if (differentWorld)
            Seed(retry);

        bool fail = true;
        var recordings = new Recording?[3];
        using var schedule = CreateSchedule(parallel,
            (world, readWorld, commands, events) => recordings[0] = Record(commands, events, fail ? 10 : 20),
            (world, readWorld, commands, events) =>
            {
                recordings[1] = Record(commands, events, fail ? 11 : 21);
                if (fail)
                {
                    // The first buffer and this buffer's spawn apply before the extension throws.
                    commands.RecordExtension<UnsupportedOp>(default);
                    commands.Spawn();
                }
            },
            (world, readWorld, commands, events) => recordings[2] = Record(commands, events, fail ? 12 : 22));

        await Assert.That(() => schedule.Run(source)).ThrowsExactly<InvalidOperationException>()
            .WithMessageContaining("Extension commands require", StringComparison.Ordinal);
        await Assert.That(source.EntityCount).IsEqualTo(3);
        await Assert.That(source.Events.Incoming<int>().ToArray()).IsEquivalentTo([-1]);
        await AssertCleared(recordings).ConfigureAwait(false);

        fail = false;
        int beforeRetry = retry.EntityCount;
        schedule.Run(retry);
        await Assert.That(retry.EntityCount).IsEqualTo(beforeRetry + 3);
        await Assert.That(retry.Events.Incoming<int>().ToArray())
            .IsEquivalentTo([20, 21, 22], CollectionOrdering.Matching);
        await AssertCleared(recordings).ConfigureAwait(false);

        schedule.Run(retry);
        await Assert.That(retry.EntityCount).IsEqualTo(beforeRetry + 6);
        if (differentWorld)
        {
            await Assert.That(source.EntityCount).IsEqualTo(3);
            await Assert.That(source.Events.Incoming<int>().ToArray()).IsEquivalentTo([-1]);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EventCommitFailure_DiscardsStagingWithoutRollingBackPlayback(bool differentWorld)
    {
        using var shared = new SharedWorld<SmallBitSet<ulong>, DefaultConfig>(ComponentRegistry.Shared.TypeInfos);
        var source = shared.CreateWorld();
        source.Events.SetIncoming<int>([-1]);
        var retry = differentWorld ? shared.CreateWorld() : source;
        var failure = new InvalidOperationException("Event buffer creation failed.");
        int failingType = SystemEventTypeRegistry.Register(() => throw failure);
        bool fail = true;
        var recordings = new Recording?[2];
        using var schedule = CreateSchedule(false,
            (world, readWorld, commands, events) => recordings[0] = Record(commands, events, fail ? 10 : 20),
            (world, readWorld, commands, events) =>
            {
                recordings[1] = Record(commands, events, fail ? 11 : 21);
                if (fail)
                {
                    // Inject a factory failure after the preceding writer has staged a valid event.
                    ref var header = ref Unsafe.As<byte, SystemEventRecord>(ref MemoryMarshal.GetReference(events.Written));
                    header.TypeId = failingType;
                }
            });

        await Assert.That(ReferenceEquals(RunAndCatch(() => schedule.Run(source)), failure)).IsTrue();
        await Assert.That(source.EntityCount).IsEqualTo(2);
        await Assert.That(source.Events.Incoming<int>().ToArray()).IsEquivalentTo([-1]);
        await AssertCleared(recordings).ConfigureAwait(false);

        fail = false;
        int beforeRetry = retry.EntityCount;
        schedule.Run(retry);
        await Assert.That(retry.EntityCount).IsEqualTo(beforeRetry + 2);
        await Assert.That(retry.Events.Incoming<int>().ToArray())
            .IsEquivalentTo([20, 21], CollectionOrdering.Matching);
        await AssertCleared(recordings).ConfigureAwait(false);

        // If the retry used another world, the source must also discard its partially staged merge.
        schedule.Run(source);
        await Assert.That(source.Events.Incoming<int>().ToArray())
            .IsEquivalentTo([20, 21], CollectionOrdering.Matching);
    }

    private static Entity Seed(World<SmallBitSet<ulong>, DefaultConfig> world)
    {
        var target = world.Spawn();
        world.AddComponent(target, new TestPosition());
        return target;
    }

    private static SystemSchedule<SmallBitSet<ulong>, DefaultConfig> CreateSchedule(
        bool parallel, params SystemRunWorldAction<SmallBitSet<ulong>, DefaultConfig>[] dispatchers)
        => new(
            [ImmutableArray.CreateRange(Enumerable.Range(0, dispatchers.Length))],
            ImmutableArray.Create(new SystemRunChunkAction<SmallBitSet<ulong>, DefaultConfig>?[dispatchers.Length]),
            ImmutableArray.CreateRange<SystemRunWorldAction<SmallBitSet<ulong>, DefaultConfig>?>(dispatchers),
            ImmutableArray.Create(new SystemMetadata<SmallBitSet<ulong>>[dispatchers.Length]),
            ImmutableArray.Create(new Paradise.Features.FeatureId[dispatchers.Length]),
            switches: null,
            parallel ? new ParallelWaveScheduler() : new SequentialWaveScheduler());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Recording Record(EntityCommandBuffer commands, SystemEventWriter events, int value)
    {
        var entity = commands.Spawn();
        commands.AddComponent(entity, new TestHealth { Current = value });
        events.Append(value);
        var state = commands.GetOrCreateExtensionState<ReferenceState>();
        var staged = new object();
        state.Value = staged;
        return new Recording(commands, events, state, new WeakReference(staged));
    }

    private static async Task AssertCleared(Recording?[] recordings)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        foreach (var recording in recordings)
        {
            // Parallel.For may skip work items after another item fails.
            if (recording is null)
                continue;
            await Assert.That(recording.Commands.IsEmpty).IsTrue();
            await Assert.That(recording.Events.Written.Length).IsEqualTo(0);
            await Assert.That(recording.State.Value).IsNull();
            await Assert.That(recording.StagedReference.IsAlive).IsFalse();
        }
    }

    private static Exception? RunAndCatch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Exception? Unwrap(Exception? exception)
        => exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.Single() : exception;

    public sealed class ReferenceState : ICommandBufferExtensionState
    {
        public object? Value { get; set; }
        public void Clear() => Value = null;
    }

    private sealed record Recording(
        EntityCommandBuffer Commands, SystemEventWriter Events, ReferenceState State, WeakReference StagedReference);

    private readonly struct UnsupportedOp : ICommandExtension;
}
