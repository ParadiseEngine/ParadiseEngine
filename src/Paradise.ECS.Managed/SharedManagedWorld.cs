using System.Collections.Immutable;

namespace Paradise.ECS;

/// <summary>Owns shared chunk resources and creates worlds with independent managed slot stores.</summary>
/// <remarks>All mutations are owner-thread-only, like the enclosed worlds.</remarks>
public sealed class SharedManagedWorld<TMask, TConfig, TInner> : IDisposable
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
    where TInner : IWorld<TMask, TConfig>
{
    private readonly ImmutableArray<ManagedTypeInfo> _managedTypes;
    private readonly Func<TConfig, ChunkManager, SharedArchetypeMetadata<TMask, TConfig>, TInner> _createInner;
    private readonly Action<TInner, TInner> _copyFrom;
    private readonly List<ManagedWorld<TMask, TConfig, TInner>> _worlds = [];
    private ThreadAffinity _threadAffinity;
    private bool _disposed;

    public SharedManagedWorld(ImmutableArray<ComponentTypeInfo> typeInfos, ImmutableArray<ManagedTypeInfo> managedTypes,
        Func<TConfig, ChunkManager, SharedArchetypeMetadata<TMask, TConfig>, TInner> createInner,
        Action<TInner, TInner> copyFrom)
        : this(typeInfos, managedTypes, createInner, copyFrom, new TConfig())
    {
    }

    public SharedManagedWorld(ImmutableArray<ComponentTypeInfo> typeInfos, ImmutableArray<ManagedTypeInfo> managedTypes,
        Func<TConfig, ChunkManager, SharedArchetypeMetadata<TMask, TConfig>, TInner> createInner,
        Action<TInner, TInner> copyFrom, TConfig config)
    {
        ArgumentNullException.ThrowIfNull(createInner);
        ArgumentNullException.ThrowIfNull(copyFrom);
        if (managedTypes.IsDefault)
            throw new ArgumentNullException(nameof(managedTypes));
        Config = config;
        _managedTypes = managedTypes;
        _createInner = createInner;
        _copyFrom = copyFrom;
        SharedMetadata = new SharedArchetypeMetadata<TMask, TConfig>(typeInfos, config);
        try
        {
            ChunkManager = ChunkManager.Create(config);
        }
        catch
        {
            SharedMetadata.Dispose();
            throw;
        }
    }

    public ChunkManager ChunkManager { get; }
    public SharedArchetypeMetadata<TMask, TConfig> SharedMetadata { get; }
    public TConfig Config { get; }

    public ManagedWorld<TMask, TConfig, TInner> CreateWorld()
    {
        _threadAffinity.Assert();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var inner = _createInner(Config, ChunkManager, SharedMetadata);
        var world = new ManagedWorld<TMask, TConfig, TInner>(inner, _managedTypes, _copyFrom);
        _worlds.Add(world);
        return world;
    }

    public void Dispose()
    {
        _threadAffinity.Assert();
        if (_disposed)
            return;
        foreach (var world in _worlds)
            world.AssertStructuralChangesAllowed(nameof(Dispose));
        foreach (var world in _worlds)
            world.Clear();
        _worlds.Clear();
        SharedMetadata.Dispose();
        ChunkManager.Dispose();
        _disposed = true;
    }
}
