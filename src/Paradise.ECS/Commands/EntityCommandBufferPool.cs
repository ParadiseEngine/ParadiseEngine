namespace Paradise.ECS;

/// <summary>Rents and replays command buffers in schedule order.</summary>
/// <remarks>
/// Only the schedule thread accesses the pool. Each work item owns one buffer, rented in
/// (wave, stable system order, chunk) order and replayed in that order after workers finish.
/// Last command wins; allocating entity IDs during playback keeps sequential and parallel results identical.
/// </remarks>
internal sealed class EntityCommandBufferPool : IDisposable
{
    private readonly List<EntityCommandBuffer> _buffers = new();
    private int _rentedCount;
    private bool _disposed;

    /// <summary>
    /// Returns the next <see cref="EntityCommandBuffer"/> in schedule order, creating one on
    /// first use. Buffers are replayed by <see cref="PlaybackAll{TMask,TConfig}"/> in rent order.
    /// </summary>
    public EntityCommandBuffer Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_rentedCount == _buffers.Count)
            _buffers.Add(new EntityCommandBuffer());
        return _buffers[_rentedCount++];
    }

    /// <summary>Plays back all buffers rented during the current run, in rent (= schedule) order.</summary>
    /// <typeparam name="TMask">The component mask type.</typeparam>
    /// <typeparam name="TConfig">The world configuration type.</typeparam>
    /// <param name="world">The world to replay commands against.</param>
    public void PlaybackAll<TMask, TConfig>(IWorld<TMask, TConfig> world)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < _rentedCount; i++)
            _buffers[i].Playback(world);
    }

    /// <summary>Clears all rented buffers and resets the rent cursor for the next run.</summary>
    public void ClearAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < _rentedCount; i++)
            _buffers[i].Clear();
        _rentedCount = 0;
    }

    /// <summary>Disposes all pooled buffers.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var ecb in _buffers)
            ecb.Dispose();
        _buffers.Clear();
        _rentedCount = 0;
    }
}
