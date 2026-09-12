namespace Paradise.ECS.Test;

public sealed class CommandBufferExtensionStateTests
{
    [Test]
    public async Task Clear_ReusesStateAndReleasesEveryStagedReference()
    {
        using var commands = new EntityCommandBuffer();
        var first = commands.GetOrCreateExtensionState<FirstState>();
        var second = commands.GetOrCreateExtensionState<SecondState>();
        first.Value = new object();
        second.Value = new object();

        commands.Clear();

        await Assert.That(first.Value).IsNull();
        await Assert.That(second.Value).IsNull();
        await Assert.That(first.ClearCount).IsEqualTo(1);
        await Assert.That(ReferenceEquals(first, commands.GetOrCreateExtensionState<FirstState>())).IsTrue();
        await Assert.That(commands.TryGetExtensionState<SecondState>(out var found)).IsTrue();
        await Assert.That(ReferenceEquals(second, found)).IsTrue();
    }

    [Test]
    public async Task State_IsPrivateToItsBuffer_AndTryGetDoesNotCreateIt()
    {
        using var first = new EntityCommandBuffer();
        using var second = new EntityCommandBuffer();
        await Assert.That(first.TryGetExtensionState<FirstState>(out var absent)).IsFalse();
        await Assert.That(absent).IsNull();
        var firstState = first.GetOrCreateExtensionState<FirstState>();
        var secondState = second.GetOrCreateExtensionState<FirstState>();
        firstState.Value = new object();
        secondState.Value = new object();

        first.Clear();

        await Assert.That(firstState.Value).IsNull();
        await Assert.That(secondState.Value).IsNotNull();
    }

    [Test]
    public async Task Dispose_ClearsOnce_AndRejectsStateAccess()
    {
        var commands = new EntityCommandBuffer();
        var state = commands.GetOrCreateExtensionState<FirstState>();
        state.Value = new object();

        commands.Dispose();
        commands.Dispose();

        await Assert.That(state.Value).IsNull();
        await Assert.That(state.ClearCount).IsEqualTo(1);
        await Assert.That(() => commands.GetOrCreateExtensionState<FirstState>()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => commands.TryGetExtensionState<FirstState>(out _)).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task FailedPlayback_CanClearStagingAndReuseBuffer()
    {
        using var shared = new SharedWorld<SmallBitSet<ulong>, DefaultConfig>(ComponentRegistry.Shared.TypeInfos);
        var world = shared.CreateWorld();
        using var commands = new EntityCommandBuffer();
        var state = commands.GetOrCreateExtensionState<FirstState>();
        state.Value = new object();
        commands.RecordExtension<UnsupportedOp>(world.Spawn());

        await Assert.That(() => commands.Playback(world)).ThrowsExactly<InvalidOperationException>();
        commands.Clear();
        await Assert.That(state.Value).IsNull();

        var placeholder = commands.Spawn();
        commands.Playback(world);
        await Assert.That(world.IsAlive(commands.Resolve(placeholder))).IsTrue();
    }

    public sealed class FirstState : ICommandBufferExtensionState
    {
        public object? Value { get; set; }
        public int ClearCount { get; private set; }

        public void Clear()
        {
            Value = null;
            ClearCount++;
        }
    }

    public sealed class SecondState : ICommandBufferExtensionState
    {
        public object? Value { get; set; }
        public void Clear() => Value = null;
    }

    private readonly struct UnsupportedOp : ICommandExtension;
}
