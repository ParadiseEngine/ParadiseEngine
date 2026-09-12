namespace Paradise.ECS;

/// <summary>Binds an entity handle to its world and resolves its current location for each operation.</summary>
/// <remarks>The handle can be stored across archetype moves but does not extend the entity or world lifetime.</remarks>
public readonly struct WorldEntity
{
    private readonly IWorld? _world;

    /// <summary>Binds an entity to the supplied world without changing it.</summary>
    public WorldEntity(IWorld world, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        Entity = entity;
    }

    /// <summary>The world that owns the entity.</summary>
    public IWorld World => _world ?? throw new InvalidOperationException("The entity handle is not bound to a world.");

    /// <summary>The entity identity, including its generation.</summary>
    public Entity Entity { get; }

    /// <summary>Returns whether the handle is bound to a world and its entity is alive.</summary>
    public bool IsAlive => _world is not null && !Entity.IsPlaceholder && _world.IsAlive(Entity);

    /// <summary>Adds a zero-valued component, a tag, or a null-valued managed component.</summary>
    public void Add<T>() where T : IEntityComponent => T.Add(RequireAliveWorld(), Entity);

    /// <summary>Adds a component with the supplied value.</summary>
    public void Add<T>(T? value) where T : IEntityComponent<T> => T.Add(RequireAliveWorld(), Entity, value);

    /// <summary>Returns whether the entity is alive and has the component or tag.</summary>
    public bool Has<T>() where T : IEntityComponent
    {
        var world = World;
        return !Entity.IsPlaceholder && world.IsAlive(Entity) && T.Has(world, Entity);
    }

    /// <summary>Removes a component or tag from the entity.</summary>
    public void Remove<T>() where T : IEntityComponent => T.Remove(RequireAliveWorld(), Entity);

    /// <summary>Reads a component value, copying unmanaged data or returning the managed object.</summary>
    public T? Get<T>() where T : IEntityComponent<T> => T.Get(RequireAliveWorld(), Entity);

    /// <summary>Replaces an existing component value.</summary>
    public void Set<T>(T? value) where T : IEntityComponent<T> => T.Set(RequireAliveWorld(), Entity, value);

    /// <summary>Returns presence and the component value, including a present managed null.</summary>
    public bool TryGet<T>(out T? value) where T : IEntityComponent<T>
    {
        var world = World;
        if (!Entity.IsPlaceholder && world.IsAlive(Entity))
            return T.TryGet(world, Entity, out value);
        value = default;
        return false;
    }

    /// <summary>Gets a writable unmanaged component reference that remains valid until the next structural change.</summary>
    /// <exception cref="InvalidOperationException">The entity or component is missing, or the component has no stored bytes.</exception>
    public ref T GetRef<T>() where T : unmanaged, IComponent
    {
        var world = RequireAliveWorld();
        if (T.Size == 0)
            throw new InvalidOperationException("Marker components do not have storage to reference.");
        return ref world.GetComponent<T>(Entity);
    }

    /// <summary>Destroys the entity, returning false if it is already dead or is a deferred placeholder.</summary>
    public bool Despawn()
    {
        var world = World;
        return !Entity.IsPlaceholder && world.Despawn(Entity);
    }

    private IWorld RequireAliveWorld()
    {
        var world = World;
        if (Entity.IsPlaceholder || !world.IsAlive(Entity))
            throw new InvalidOperationException($"Entity {Entity} is not alive in this world.");
        return world;
    }
}
