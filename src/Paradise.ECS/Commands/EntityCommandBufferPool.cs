namespace Paradise.ECS;

/// <summary>
/// Thread-safe pool of <see cref="EntityCommandBuffer"/> instances.
/// Each thread that calls <see cref="Get"/> receives its own dedicated buffer,
/// avoiding contention during parallel system execution. After all work completes,
/// <see cref="PlaybackAll{TMask,TConfig}"/> replays every buffer sequentially
/// and <see cref="ClearAll"/> resets them for the next run.
/// </summary>
internal sealed class EntityCommandBufferPool : IDisposable
{
    private readonly EntityIdAllocator _allocator;
    private readonly ThreadLocal<EntityCommandBuffer> _threadLocal;
    private readonly List<EntityCommandBuffer> _all = new();
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new pool backed by the given entity ID allocator.
    /// </summary>
    /// <param name="allocator">The allocator used to reserve entity IDs in each buffer.</param>
    public EntityCommandBufferPool(EntityIdAllocator allocator)
    {
        _allocator = allocator;
        _threadLocal = new ThreadLocal<EntityCommandBuffer>(CreateBuffer, trackAllValues: false);
    }

    /// <summary>
    /// Returns the <see cref="EntityCommandBuffer"/> for the current thread.
    /// Creates one on first access.
    /// </summary>
    public EntityCommandBuffer Get()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _threadLocal.Value!;
    }

    /// <summary>
    /// Plays back all buffers that were accessed during the current run,
    /// in the order they were created.
    /// </summary>
    /// <typeparam name="TMask">The component mask type.</typeparam>
    /// <typeparam name="TConfig">The world configuration type.</typeparam>
    /// <param name="world">The world to replay commands against.</param>
    public void PlaybackAll<TMask, TConfig>(IWorld<TMask, TConfig> world)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            foreach (var ecb in _all)
                ecb.Playback(world);
        }
    }

    /// <summary>
    /// Clears all buffers for reuse in the next run.
    /// </summary>
    public void ClearAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            foreach (var ecb in _all)
                ecb.Clear();
        }
    }

    /// <summary>
    /// Disposes the thread-local storage and all pooled buffers.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _threadLocal.Dispose();
        lock (_lock)
        {
            foreach (var ecb in _all)
                ecb.Dispose();
            _all.Clear();
        }
    }

    private EntityCommandBuffer CreateBuffer()
    {
        var ecb = new EntityCommandBuffer(_allocator);
        lock (_lock)
        {
            _all.Add(ecb);
        }
        return ecb;
    }
}
