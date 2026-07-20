namespace Paradise.ECS;

/// <summary>
/// Common interface for ECS worlds, providing entity lifecycle, component access,
/// and chunk management operations. Implemented by both World and TaggedWorld.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public interface IWorld<TMask, TConfig>
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    /// <summary>
    /// Creates a new entity with no components (or with EntityTags for TaggedWorld).
    /// </summary>
    /// <returns>The created entity handle.</returns>
    Entity Spawn();

    /// <summary>
    /// Destroys an entity and removes it from its archetype.
    /// </summary>
    /// <param name="entity">The entity to destroy.</param>
    /// <returns>True if the entity was destroyed, false if it was already dead or invalid.</returns>
    bool Despawn(Entity entity);

    /// <summary>
    /// Checks if an entity is currently alive.
    /// </summary>
    /// <param name="entity">The entity to check.</param>
    /// <returns>True if the entity is alive.</returns>
    bool IsAlive(Entity entity);

    /// <summary>
    /// Gets the number of currently alive entities.
    /// </summary>
    int EntityCount { get; }

    /// <summary>
    /// Gets a reference to a component on an entity.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <returns>A reference to the component.</returns>
    /// <exception cref="InvalidOperationException">Entity doesn't have the component.</exception>
    ref T GetComponent<T>(Entity entity) where T : unmanaged, IComponent;

    /// <summary>
    /// Checks if an entity has a specific component.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <returns>True if the entity has the component.</returns>
    bool HasComponent<T>(Entity entity) where T : unmanaged, IComponent;

    /// <summary>
    /// Adds a component to an entity. This is a structural change that may move the entity.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <param name="value">The component value.</param>
    /// <exception cref="InvalidOperationException">Entity is not alive or already has the component.</exception>
    void AddComponent<T>(Entity entity, T value = default) where T : unmanaged, IComponent;

    /// <summary>
    /// Removes a component from an entity. This is a structural change that may move the entity.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <exception cref="InvalidOperationException">Entity is not alive or doesn't have the component.</exception>
    void RemoveComponent<T>(Entity entity) where T : unmanaged, IComponent;

    /// <summary>
    /// Gets the chunk manager for memory allocation and chunk access.
    /// </summary>
    ChunkManager ChunkManager { get; }

    /// <summary>
    /// Gets the world's deferred event buffers (see <c>docs/system-events.md</c>): each event
    /// type's INCOMING events (produced last frame). Participates in <c>CopyFrom</c>/snapshots.
    /// </summary>
    WorldEventStore Events { get; }

    /// <summary>
    /// Gets the entity manager for entity lifecycle and location tracking.
    /// </summary>
    IEntityManager EntityManager { get; }

    /// <summary>
    /// Gets the archetype registry for queries and archetype management.
    /// </summary>
    ArchetypeRegistry<TMask, TConfig> ArchetypeRegistry { get; }

    /// <summary>
    /// Gets the thread-safe entity ID allocator backing this world's entity manager.
    /// </summary>
    EntityIdAllocator EntityIdAllocator { get; }

    /// <summary>
    /// Creates a new entity using the provided builder.
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The component builder with initial components.</param>
    /// <returns>The created entity handle.</returns>
    Entity CreateEntity<TBuilder>(TBuilder builder) where TBuilder : unmanaged, IComponentsBuilder;

    /// <summary>
    /// Overwrites all components on an existing entity with the builder's components.
    /// Any existing components are discarded. The entity must already exist in this world.
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="entity">The existing entity handle.</param>
    /// <param name="builder">The component builder with components to set.</param>
    /// <returns>The entity handle.</returns>
    Entity OverwriteEntity<TBuilder>(Entity entity, TBuilder builder) where TBuilder : unmanaged, IComponentsBuilder;

    /// <summary>
    /// Adds multiple components to an existing entity using the provided builder.
    /// Existing components are preserved. This is a structural change that moves the entity.
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="entity">The existing entity handle.</param>
    /// <param name="builder">The component builder with components to add or update.</param>
    /// <returns>The entity handle.</returns>
    Entity AddComponents<TBuilder>(Entity entity, TBuilder builder) where TBuilder : unmanaged, IComponentsBuilder;

    /// <summary>
    /// Adds a component to an entity using raw bytes. This is a structural change that may move the entity.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="componentId">The component type ID.</param>
    /// <param name="data">The raw component data bytes.</param>
    void AddComponentRaw(Entity entity, ComponentId componentId, ReadOnlySpan<byte> data);

    /// <summary>
    /// Removes a component from an entity using a raw component ID.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="componentId">The component type ID.</param>
    void RemoveComponentRaw(Entity entity, ComponentId componentId);

    /// <summary>
    /// Sets a component value on an entity using raw bytes. This is NOT a structural change.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="componentId">The component type ID.</param>
    /// <param name="data">The raw component data bytes.</param>
    void SetComponentRaw(Entity entity, ComponentId componentId, ReadOnlySpan<byte> data);

    /// <summary>
    /// Removes all entities from this world.
    /// </summary>
    void Clear();

    /// <summary>
    /// Marks whether a <see cref="SystemSchedule{TMask,TConfig}"/> run is currently in progress
    /// on this world. Set by the schedule around wave execution (and cleared before ECB
    /// playback). While set, DEBUG builds reject direct structural changes
    /// (Spawn/CreateEntity/Despawn/Add-/RemoveComponent/Clear/…) with
    /// <see cref="InvalidOperationException"/> — mid-run structural changes must be recorded on
    /// an injected <see cref="EntityCommandBuffer"/> instead. The checks are compiled out in
    /// Release builds, where this flag has no observable effect.
    /// </summary>
    /// <param name="running">True while schedule waves are executing; false otherwise.</param>
    void SetSystemRunInProgress(bool running);
}
