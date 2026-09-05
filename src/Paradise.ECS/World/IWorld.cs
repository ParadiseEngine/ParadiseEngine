namespace Paradise.ECS;

/// <summary>
/// Common interface for ECS worlds, providing entity lifecycle, component access,
/// and chunk management operations. Implemented by both World and TaggedWorld.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public interface IWorld<TMask, TConfig> : IEntityComponentAccess
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
    /// Gets the world's deferred event buffers: each event
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
    /// Creates a new entity whose archetype is a mask assembled at RUNTIME, with every component
    /// at its zero default.
    ///
    /// The counterpart to <see cref="CreateEntity{TBuilder}"/> for a caller that cannot name its
    /// component set at compile time — a scene loader composing an entity out of whatever
    /// components the authored document happens to carry. <see cref="IComponentsBuilder"/> builds
    /// its mask by nesting generic structs, so the set has to be a literal chain of type
    /// arguments; a mask is the same information as a value.
    ///
    /// Zero-initialized for the same reason <c>EnsureComponent</c> is: chunk memory is cleared on
    /// allocation, so an unwritten component reads as <c>default</c> rather than as whatever the
    /// last occupant of that slot left behind. Seed values afterwards through
    /// <c>GetComponent&lt;T&gt;</c>.
    /// </summary>
    /// <param name="mask">The component set the entity is created with. An empty mask places the
    /// entity in the empty archetype, exactly as <see cref="Spawn"/> does.</param>
    /// <returns>The created entity handle.</returns>
    Entity CreateEntity(in TMask mask);

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
    /// (Spawn/CreateEntity/Despawn/Add-/RemoveComponent/Add-/RemoveTag/Clear/…) with
    /// <see cref="InvalidOperationException"/> — mid-run structural changes must be recorded on
    /// an injected <see cref="EntityCommandBuffer"/> instead. The checks are compiled out in
    /// Release builds, where this flag has no observable effect.
    /// </summary>
    /// <param name="running">True while schedule waves are executing; false otherwise.</param>
    void SetSystemRunInProgress(bool running);
}
