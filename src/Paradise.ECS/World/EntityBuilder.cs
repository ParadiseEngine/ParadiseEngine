using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>
/// Interface for component builders used in fluent entity creation.
/// Each builder collects component types and writes component data.
/// </summary>
public interface IComponentsBuilder
{
    /// <summary>
    /// Collects component type IDs into the component mask.
    /// </summary>
    /// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
    /// <param name="mask">The mask to add component types to.</param>
    void CollectTypes<TMask>(ref TMask mask)
        where TMask : unmanaged, IBitSet<TMask>;

    /// <summary>
    /// Writes component data to the entity's chunk location.
    /// </summary>
    /// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
    /// <typeparam name="TConfig">The world configuration type.</typeparam>
    /// <typeparam name="TChunkManager">The chunk manager type.</typeparam>
    /// <param name="chunkManager">The chunk manager for memory access.</param>
    /// <param name="layout">The archetype layout with component offsets.</param>
    /// <param name="chunkHandle">The chunk where data should be written.</param>
    /// <param name="indexInChunk">The entity's index within the chunk.</param>
    void WriteComponents<TMask, TConfig, TChunkManager>(
        TChunkManager chunkManager,
        ImmutableArchetypeLayout<TMask, TConfig> layout,
        ChunkHandle chunkHandle,
        int indexInChunk)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
        where TChunkManager : IChunkManager;
}

/// <summary>
/// Base builder for creating entities with no initial components.
/// Start entity creation with <see cref="Create"/>.
/// </summary>
public readonly struct EntityBuilder : IComponentsBuilder
{
    /// <summary>
    /// Creates a new empty entity builder.
    /// </summary>
    /// <returns>A new entity builder.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityBuilder Create() => new();

    /// <inheritdoc cref="EnsureComponentSet{TComponentSet, TInnerBuilder}"/>
    /// <summary>
    /// Ensures every component of an <see cref="IComponentSet"/> — typically a queryable —
    /// exists on the entity with its default value.
    ///
    /// This is how an entity is built from what the systems actually query for:
    /// <code>
    /// world.CreateEntity(EntityBuilder.Create()
    ///     .EnsureFrom&lt;PlayerPenguins&gt;()
    ///     .EnsureFrom&lt;SwimPenguins&gt;()
    ///     .Add(new Position { Value = spawn }));
    /// </code>
    /// Chain one call per queryable to compose the union; the mask is a set, so a component two
    /// queryables share costs nothing and a later <c>Add</c> just seeds a value over the default.
    ///
    /// NOTE: this lives on each builder struct rather than beside Add/Ensure in
    /// <see cref="ComponentsBuilderExtensions"/> because an extension member whose type parameter
    /// carries <c>allows ref struct</c> is not found by extension lookup — and queryables are ref
    /// structs, so that anti-constraint is required. Moving it back into the extension block
    /// compiles the declaration fine and then fails every call site with CS1061.
    /// </summary>
    /// <typeparam name="TComponentSet">The component set to take types from.</typeparam>
    /// <returns>A new builder with the set's component types added.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EnsureComponentSet<TComponentSet, EntityBuilder> EnsureFrom<TComponentSet>()
        where TComponentSet : IComponentSet, allows ref struct
        => new() { InnerBuilder = this };

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CollectTypes<TMask>(ref TMask mask)
        where TMask : unmanaged, IBitSet<TMask>
    {
        // No components to add
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteComponents<TMask, TConfig, TChunkManager>(
        TChunkManager chunkManager,
        ImmutableArchetypeLayout<TMask, TConfig> layout,
        ChunkHandle chunkHandle,
        int indexInChunk)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
        where TChunkManager : IChunkManager
    {
        // No components to write
    }
}

/// <summary>
/// Builder that wraps an inner builder and adds a component value.
/// Created by calling the Add extension method on a builder.
/// </summary>
/// <typeparam name="TComponent">The component type to add.</typeparam>
/// <typeparam name="TInnerBuilder">The wrapped builder type.</typeparam>
public readonly struct WithComponent<TComponent, TInnerBuilder> : IComponentsBuilder
    where TComponent : unmanaged, IComponent
    where TInnerBuilder : unmanaged, IComponentsBuilder
{
    /// <summary>
    /// The component value to add.
    /// </summary>
    public TComponent Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        init;
    }

    /// <summary>
    /// The inner builder that this wraps.
    /// </summary>
    public TInnerBuilder InnerBuilder
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        init;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CollectTypes<TMask>(ref TMask mask)
        where TMask : unmanaged, IBitSet<TMask>
    {
        InnerBuilder.CollectTypes(ref mask);
        mask = mask.Set(TComponent.TypeId);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteComponents<TMask, TConfig, TChunkManager>(
        TChunkManager chunkManager,
        ImmutableArchetypeLayout<TMask, TConfig> layout,
        ChunkHandle chunkHandle,
        int indexInChunk)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
        where TChunkManager : IChunkManager
    {
        // Write inner components first
        InnerBuilder.WriteComponents(chunkManager, layout, chunkHandle, indexInChunk);

        // Skip writes for zero-size tag components to avoid corrupting memory at offset 0.
        // Empty structs have sizeof=1 in C#, so writing default(TagComponent) would write
        // 1 byte at offset 0 (since GetEntityComponentOffset returns 0 for size-0 components).
        if (TComponent.Size == 0)
            return;

        // Write this component
        int offset = layout.GetBaseOffset(TComponent.TypeId) + indexInChunk * TComponent.Size;
        chunkManager.GetBytes(chunkHandle).GetRef<TComponent>(offset) = Value;
    }

    /// <inheritdoc cref="EntityBuilder.EnsureFrom{TComponentSet}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EnsureComponentSet<TComponentSet, WithComponent<TComponent, TInnerBuilder>> EnsureFrom<TComponentSet>()
        where TComponentSet : IComponentSet, allows ref struct
        => new() { InnerBuilder = this };
}

/// <summary>
/// Builder that wraps an inner builder and ensures a component type exists with default value.
/// Created by calling the Ensure extension method on a builder.
/// Unlike WithComponent, this doesn't store a value - it relies on zero-initialized chunk memory.
/// </summary>
/// <typeparam name="TComponent">The component type to ensure.</typeparam>
/// <typeparam name="TInnerBuilder">The wrapped builder type.</typeparam>
public readonly struct EnsureComponent<TComponent, TInnerBuilder> : IComponentsBuilder
    where TComponent : unmanaged, IComponent
    where TInnerBuilder : unmanaged, IComponentsBuilder
{
    /// <summary>
    /// The inner builder that this wraps.
    /// </summary>
    public TInnerBuilder InnerBuilder
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        init;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CollectTypes<TMask>(ref TMask mask)
        where TMask : unmanaged, IBitSet<TMask>
    {
        InnerBuilder.CollectTypes(ref mask);
        mask = mask.Set(TComponent.TypeId);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteComponents<TMask, TConfig, TChunkManager>(
        TChunkManager chunkManager,
        ImmutableArchetypeLayout<TMask, TConfig> layout,
        ChunkHandle chunkHandle,
        int indexInChunk)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
        where TChunkManager : IChunkManager
    {
        // Write inner components first
        InnerBuilder.WriteComponents(chunkManager, layout, chunkHandle, indexInChunk);
        // No write needed - chunk memory is zero-initialized, so component has default value
    }

    /// <inheritdoc cref="EntityBuilder.EnsureFrom{TComponentSet}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EnsureComponentSet<TComponentSet, EnsureComponent<TComponent, TInnerBuilder>> EnsureFrom<TComponentSet>()
        where TComponentSet : IComponentSet, allows ref struct
        => new() { InnerBuilder = this };
}

/// <summary>
/// Builder that wraps an inner builder and ensures every component of an
/// <see cref="IComponentSet"/> exists with its default value — the whole set at once, where
/// <see cref="EnsureComponent{TComponent, TInnerBuilder}"/> does one.
///
/// Created by calling the EnsureFrom extension method on a builder. Like EnsureComponent it
/// stores no values and relies on zero-initialized chunk memory.
/// </summary>
/// <typeparam name="TComponentSet">The component set to take types from — typically a queryable.</typeparam>
/// <typeparam name="TInnerBuilder">The wrapped builder type.</typeparam>
public readonly struct EnsureComponentSet<TComponentSet, TInnerBuilder> : IComponentsBuilder
    where TComponentSet : IComponentSet, allows ref struct
    where TInnerBuilder : unmanaged, IComponentsBuilder
{
    /// <summary>
    /// The inner builder that this wraps.
    /// </summary>
    public TInnerBuilder InnerBuilder
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        init;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CollectTypes<TMask>(ref TMask mask)
        where TMask : unmanaged, IBitSet<TMask>
    {
        InnerBuilder.CollectTypes(ref mask);
        // Static dispatch, so no instance of TComponentSet is ever needed — which is what lets a
        // ref struct queryable be used as the type argument.
        TComponentSet.CollectComponentTypes(ref mask);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteComponents<TMask, TConfig, TChunkManager>(
        TChunkManager chunkManager,
        ImmutableArchetypeLayout<TMask, TConfig> layout,
        ChunkHandle chunkHandle,
        int indexInChunk)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
        where TChunkManager : IChunkManager
    {
        // Write inner components first
        InnerBuilder.WriteComponents(chunkManager, layout, chunkHandle, indexInChunk);
        // No writes - chunk memory is zero-initialized, so every component of the set is default.
        // A component that needs a seeded value is written by chaining Add after this, which
        // overwrites the default rather than duplicating the type in the mask.
    }

    /// <inheritdoc cref="EntityBuilder.EnsureFrom{TComponentSet}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EnsureComponentSet<TOtherComponentSet, EnsureComponentSet<TComponentSet, TInnerBuilder>> EnsureFrom<TOtherComponentSet>()
        where TOtherComponentSet : IComponentSet, allows ref struct
        => new() { InnerBuilder = this };
}

/// <summary>
/// Extension providing fluent Add and Ensure methods for component builders.
/// </summary>
public static class ComponentsBuilderExtensions
{
    extension<TBuilder>(TBuilder builder)
        where TBuilder : unmanaged, IComponentsBuilder
    {
        /// <summary>
        /// Adds a component to the entity being built.
        /// </summary>
        /// <typeparam name="TComponent">The component type to add.</typeparam>
        /// <param name="value">The component value.</param>
        /// <returns>A new builder with the component added.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public WithComponent<TComponent, TBuilder> Add<TComponent>(TComponent value = default)
            where TComponent : unmanaged, IComponent
        {
            return new WithComponent<TComponent, TBuilder>
            {
                Value = value,
                InnerBuilder = builder
            };
        }

        /// <summary>
        /// Ensures a component type exists on the entity with its default (zero-initialized) value.
        /// Use this for components where you don't need to specify an initial value.
        /// </summary>
        /// <typeparam name="TComponent">The component type to ensure.</typeparam>
        /// <returns>A new builder with the component type added.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EnsureComponent<TComponent, TBuilder> Ensure<TComponent>()
            where TComponent : unmanaged, IComponent
        {
            return new EnsureComponent<TComponent, TBuilder>
            {
                InnerBuilder = builder
            };
        }

        // EnsureFrom is deliberately NOT here — see the note on EntityBuilder.EnsureFrom. An
        // extension member with an `allows ref struct` type parameter is skipped by extension
        // lookup, and queryables are ref structs, so it lives on each builder struct instead.
    }
}
