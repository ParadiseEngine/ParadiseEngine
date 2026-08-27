namespace Paradise.ECS;

/// <summary>
/// Non-generic component access operations shared by world implementations.
/// </summary>
public interface IEntityComponentAccess
{
    /// <summary>Gets a reference to a component on an entity.</summary>
    ref T GetComponent<T>(Entity entity) where T : unmanaged, IComponent;

    /// <summary>Returns whether the entity is alive and has the component.</summary>
    bool HasComponent<T>(Entity entity) where T : unmanaged, IComponent;

    /// <summary>Copies a component value when it is available.</summary>
    bool TryGetComponent<T>(Entity entity, out T value) where T : unmanaged, IComponent;

    /// <summary>Sets a component value when it is available.</summary>
    bool TrySetComponent<T>(Entity entity, T value) where T : unmanaged, IComponent;
}

/// <summary>
/// Read-only component access for an arbitrary entity handle.
/// </summary>
/// <typeparam name="T">The component type.</typeparam>
public readonly struct EntityComponentReader<T>
    where T : unmanaged, IComponent
{
    private readonly IEntityComponentAccess _world;

    /// <summary>Creates a reader over the specified world.</summary>
    public EntityComponentReader(IEntityComponentAccess world) => _world = world;

    /// <summary>Gets a read-only component reference for the specified entity.</summary>
    public ref readonly T this[Entity entity] => ref _world.GetComponent<T>(entity);

    /// <summary>Returns whether the entity is alive and has the component.</summary>
    public bool Has(Entity entity) => !entity.IsPlaceholder && _world.HasComponent<T>(entity);

    /// <summary>Copies the component value when the entity is alive and has it.</summary>
    public bool TryGet(Entity entity, out T value) => _world.TryGetComponent(entity, out value);
}

/// <summary>
/// Writable component access for an arbitrary entity handle.
/// </summary>
/// <typeparam name="T">The component type.</typeparam>
public readonly struct EntityComponentWriter<T>
    where T : unmanaged, IComponent
{
    private readonly IEntityComponentAccess _world;

    /// <summary>Creates a writer over the specified world.</summary>
    public EntityComponentWriter(IEntityComponentAccess world) => _world = world;

    /// <summary>Gets a writable component reference for the specified entity.</summary>
    public ref T this[Entity entity] => ref _world.GetComponent<T>(entity);

    /// <summary>Sets the component value. Throws for stale handles or missing components.</summary>
    public void Set(Entity entity, T value) => _world.GetComponent<T>(entity) = value;

    /// <summary>Sets the component value when the entity is alive and has it.</summary>
    public bool TrySet(Entity entity, T value) => _world.TrySetComponent(entity, value);
}
