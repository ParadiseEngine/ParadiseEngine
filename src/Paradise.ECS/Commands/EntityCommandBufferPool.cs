namespace Paradise.ECS;

/// <summary>
/// Pool of <see cref="EntityCommandBuffer"/> instances handed out in SCHEDULE order.
/// <see cref="SystemSchedule{TMask,TConfig}"/> rents one buffer per work item at dispatch time —
/// on the schedule thread, in (wave index, position-in-wave, chunk index) order, with the wave's
/// system order (stable system IDs resolved at Build time) breaking ties within a wave. A work
/// item runs on exactly one thread and iterates sequentially, so each buffer is single-threaded
/// and internally deterministic by construction.
/// </summary>
/// <remarks>
/// <see cref="PlaybackAll{TMask,TConfig}"/> replays buffers in rent order, which IS the schedule
/// order — commands apply as if the schedule had run serially. Cross-system conflicts are
/// well-defined (last in schedule order wins), and <see cref="ParallelWaveScheduler"/> and
/// <see cref="SequentialWaveScheduler"/> produce identical worlds, including entity IDs
/// (allocation is deferred to playback — see <see cref="EntityCommandBuffer.Spawn"/>).
/// <para>
/// NOT thread-safe: <see cref="Rent"/>/<see cref="PlaybackAll{TMask,TConfig}"/>/<see cref="ClearAll"/>
/// must be called from the schedule thread only. Wave workers never touch the pool — each work
/// item carries its pre-rented buffer.
/// </para>
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

    /// <summary>
    /// Plays back all buffers rented during the current run, in rent (= schedule) order.
    /// </summary>
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

    /// <summary>
    /// Clears all rented buffers and resets the rent cursor for the next run.
    /// </summary>
    public void ClearAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < _rentedCount; i++)
            _buffers[i].Clear();
        _rentedCount = 0;
    }

    /// <summary>
    /// Disposes all pooled buffers.
    /// </summary>
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
