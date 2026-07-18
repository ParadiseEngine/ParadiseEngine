namespace Paradise.ECS;

/// <summary>
/// Base interface for all ECS systems. Provides the compile-time system ID
/// used for scheduling and dispatch. Implemented automatically by the source generator.
/// </summary>
public interface ISystem
{
    /// <summary>
    /// The unique system ID assigned at compile time by the source generator.
    /// </summary>
    static abstract int SystemId { get; }
}

/// <summary>
/// Marker interface for per-entity systems.
/// Implementing this interface on a <c>ref partial struct</c> enables source generator discovery.
/// The generator will auto-generate the constructor, SystemId, and <c>RunChunk</c> method.
/// </summary>
/// <remarks>
/// <para>
/// Fields declare component access:
/// <list type="bullet">
///   <item><c>ref T</c> where T is a [Component] — writable per-entity access</item>
///   <item><c>ref readonly T</c> where T is a [Component] — read-only per-entity access</item>
///   <item><c>{Prefix}Entity</c> where {Prefix} is a [Queryable] — composition access via generated Data type</item>
///   <item><c>{Prefix}Singleton</c> where {Prefix} is a [Queryable(Singleton = true)] —
///     the queryable resolved once per dispatch against exactly one entity</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public ref partial struct GravitySystem : IEntitySystem
/// {
///     public ref Velocity Velocity;
///     public void Execute()
///     {
///         Velocity = new(Velocity.X, Velocity.Y - 9.8f);
///     }
/// }
///
/// public ref partial struct MovementSystem : IEntitySystem
/// {
///     public MovableEntity Movable;
///     public void Execute()
///     {
///         Movable.Position = new(Movable.Position.X + Movable.Velocity.X,
///                                Movable.Position.Y + Movable.Velocity.Y);
///     }
/// }
/// </code>
/// </example>
public interface IEntitySystem : ISystem
{
    /// <summary>
    /// Executes this system for a single entity.
    /// Called once per entity that matches the system's query.
    /// </summary>
    void Execute();
}

/// <summary>
/// Marker interface for per-chunk systems.
/// Implementing this interface on a <c>ref partial struct</c> enables source generator discovery.
/// The generator will auto-generate the constructor, SystemId, and <c>RunChunk</c> method.
/// </summary>
/// <remarks>
/// <para>
/// Fields declare component access:
/// <list type="bullet">
///   <item><c>Span&lt;T&gt;</c> where T is a [Component] — writable batch access</item>
///   <item><c>ReadOnlySpan&lt;T&gt;</c> where T is a [Component] — read-only batch access</item>
///   <item><c>{Prefix}Chunk</c> where {Prefix} is a [Queryable] — composition access via generated ChunkData type</item>
///   <item><c>{Prefix}Singleton</c> where {Prefix} is a [Queryable(Singleton = true)] —
///     the queryable resolved once per dispatch against exactly one entity</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public ref partial struct GravityBatchSystem : IChunkSystem
/// {
///     public Span&lt;Velocity&gt; Velocities;
///     public void ExecuteChunk()
///     {
///         for (int i = 0; i &lt; Velocities.Length; i++)
///             Velocities[i] = new(Velocities[i].X, Velocities[i].Y - 9.8f);
///     }
/// }
///
/// public ref partial struct BatchMovementSystem : IChunkSystem
/// {
///     public MovableChunk Movable;
///     public void ExecuteChunk()
///     {
///         var positions = Movable.PositionSpan;
///         var velocities = Movable.VelocitySpan;
///         for (int i = 0; i &lt; Movable.EntityCount; i++)
///             positions[i] = new(positions[i].X + velocities[i].X,
///                                positions[i].Y + velocities[i].Y);
///     }
/// }
/// </code>
/// </example>
public interface IChunkSystem : ISystem
{
    /// <summary>
    /// Executes this system for a chunk of entities.
    /// Called once per chunk that matches the system's query.
    /// </summary>
    void ExecuteChunk();
}

/// <summary>
/// Marker interface for whole-world systems: <see cref="Execute"/> is called ONCE per schedule
/// run and sees every matching entity across all chunks through segment collections — the form
/// for global work (pairwise physics, gather/solve/scatter) that per-entity/per-chunk systems
/// cannot express. Implementing this on a <c>ref partial struct</c> enables source generator
/// discovery; the generator emits the constructor, SystemId, and <c>RunWorld</c> dispatch.
/// </summary>
/// <remarks>
/// <para>
/// Fields declare component access:
/// <list type="bullet">
///   <item><c>{Prefix}Segments</c> where {Prefix} is a [Queryable] — flat
///     <see cref="ComponentSegments{T,TMask,TConfig}"/>/<see cref="ReadOnlyComponentSegments{T,TMask,TConfig}"/>
///     views per component, index-correlated across the queryable's components</item>
///   <item><c>{Prefix}Singleton</c> where {Prefix} is a [Queryable(Singleton = true)] —
///     the queryable resolved once per run against exactly one entity</item>
///   <item><c>EntityCommandBuffer</c> — deferred structural changes</item>
/// </list>
/// Inline <c>ref T</c>/<c>Span&lt;T&gt;</c> fields are not valid on world systems.
/// Under <c>[assembly: SnapshotReadSystems]</c>, read-only components bind to the READ world
/// passed to <c>SystemSchedule.Run(readWorld)</c> (previous tick); writable components bind to
/// the write world.
/// </para>
/// </remarks>
public interface IWorldSystem : ISystem
{
    /// <summary>Executes this system once over the whole world.</summary>
    void Execute();
}

/// <summary>
/// Typed system interface providing a static <c>RunChunk</c> dispatch method.
/// Implemented by source-generated system partials to enable delegate-based dispatch
/// without a generated switch statement.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public interface ISystem<TMask, TConfig> : ISystem
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    /// <summary>
    /// The compile-time metadata for this system, including access masks and dependency edges.
    /// </summary>
    static abstract SystemMetadata<TMask> Metadata { get; }

    /// <summary>
    /// Executes this system over a single chunk. Called by the scheduler.
    /// </summary>
    /// <param name="world">The world containing the entities (the WRITE world).</param>
    /// <param name="chunk">The chunk handle to process.</param>
    /// <param name="readChunkManager">Chunk memory source for read-only bindings; equals
    /// <c>world.ChunkManager</c> in classic execution. Only snapshot-mode codegen
    /// (<c>[assembly: SnapshotReadSystems]</c>) consumes it.</param>
    /// <param name="readChunk">Chunk in the read source corresponding to <paramref name="chunk"/>.</param>
    /// <param name="readWorld">The immutable read world in snapshot mode
    /// (<c>SystemSchedule.Run(readWorld)</c>), or null under classic <c>Run()</c>. Consumed by
    /// snapshot-mode codegen to pair chunks OUTSIDE this system's own query — e.g. resolving the
    /// read-world copy of a <c>{Prefix}Singleton</c> field's entity.</param>
    /// <param name="layout">The archetype layout describing component offsets.</param>
    /// <param name="entityCount">The number of entities in the chunk.</param>
    /// <param name="commands">The entity command buffer for deferred structural changes.</param>
    static abstract void RunChunk(
        IWorld<TMask, TConfig> world,
        ChunkHandle chunk,
        ChunkManager readChunkManager,
        ChunkHandle readChunk,
        IWorld<TMask, TConfig>? readWorld,
        ImmutableArchetypeLayout<TMask, TConfig> layout,
        int entityCount,
        EntityCommandBuffer commands);
}

/// <summary>
/// Typed dispatch interface for source-generated <see cref="IWorldSystem"/> partials: a static
/// <c>RunWorld</c> executed once per schedule run (not per chunk).
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public interface IWorldSystemRunner<TMask, TConfig> : ISystem
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    /// <summary>The compile-time metadata for this system (masks used for wave scheduling).</summary>
    static abstract SystemMetadata<TMask> Metadata { get; }

    /// <summary>
    /// Executes this system once over the whole world. Called by the scheduler.
    /// </summary>
    /// <param name="world">The WRITE world.</param>
    /// <param name="readWorld">The immutable read world in snapshot mode
    /// (<c>SystemSchedule.Run(readWorld)</c>), or null under classic <c>Run()</c> — generated
    /// bodies fall back to binding reads to <paramref name="world"/>.</param>
    /// <param name="commands">The entity command buffer for deferred structural changes.</param>
    static abstract void RunWorld(
        IWorld<TMask, TConfig> world,
        IWorld<TMask, TConfig>? readWorld,
        EntityCommandBuffer commands);
}
