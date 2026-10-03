using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>
/// Interface for component builders used in fluent entity creation.
/// Each builder collects component types and writes component data.
/// </summary>
public interface IComponentsBuilder
{
    /// <summary>Collects component type IDs into the component mask.</summary>
    /// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
    /// <param name="mask">The mask to add component types to.</param>
    void CollectTypes<TMask>(ref TMask mask)
        where TMask : unmanaged, IBitSet<TMask>;

    /// <summary>Writes component data to the entity's chunk location.</summary>
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
    /// <summary>Creates a new empty entity builder.</summary>
    /// <returns>A new entity builder.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityBuilder Create() => new();

    /// <inheritdoc cref="EnsureComponentSet{TComponentSet, TInnerBuilder}"/>
    /// <summary>Ensures all component types in a set, typically a queryable, are included without adding value writes.</summary>
    /// <remarks>
    /// Chain sets to form their union; a later Add seeds a value without duplicating a component.
    /// <code>
    /// world.CreateEntity(EntityBuilder.Create()
    ///     .EnsureFrom&lt;PlayerPenguins&gt;()
    ///     .EnsureFrom&lt;SwimPenguins&gt;()
    ///     .Add(new Position { Value = spawn }));
    /// </code>
    /// This method lives on each builder because extension lookup skips type parameters with
    /// <c>allows ref struct</c>, which queryables require; an extension declaration causes CS1061 at call sites.
    /// </remarks>
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
    /// <summary>The component value to add.</summary>
    public TComponent Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        init;
    }

    /// <summary>The inner builder that this wraps.</summary>
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
        InnerBuilder.WriteComponents(chunkManager, layout, chunkHandle, indexInChunk);

        // Skip writes for zero-size tag components to avoid corrupting memory at offset 0.
        // Empty structs have sizeof=1 in C#, so writing default(TagComponent) would write
        // 1 byte at offset 0 (since GetBaseOffset returns 0 for size-0 components).
        if (TComponent.Size == 0)
            return;

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
/// Builder that wraps an inner builder and ensures a component type is included without writing it.
/// Created by calling the Ensure extension method on a builder.
/// Unlike WithComponent, this stores no value and preserves values assigned by the inner builder.
/// </summary>
/// <typeparam name="TComponent">The component type to ensure.</typeparam>
/// <typeparam name="TInnerBuilder">The wrapped builder type.</typeparam>
public readonly struct EnsureComponent<TComponent, TInnerBuilder> : IComponentsBuilder
    where TComponent : unmanaged, IComponent
    where TInnerBuilder : unmanaged, IComponentsBuilder
{
    /// <summary>The inner builder that this wraps.</summary>
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
        InnerBuilder.WriteComponents(chunkManager, layout, chunkHandle, indexInChunk);
        // Keep any value written by the inner builder; this wrapper adds only the component type.
    }

    /// <inheritdoc cref="EntityBuilder.EnsureFrom{TComponentSet}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EnsureComponentSet<TComponentSet, EnsureComponent<TComponent, TInnerBuilder>> EnsureFrom<TComponentSet>()
        where TComponentSet : IComponentSet, allows ref struct
        => new() { InnerBuilder = this };
}

/// <summary>
/// Builder that wraps an inner builder and ensures every component of an
/// <see cref="IComponentSet"/> is included without adding value writes — the whole set at once, where
/// <see cref="EnsureComponent{TComponent, TInnerBuilder}"/> does one.
///
/// Created by calling the EnsureFrom instance method on a builder. Like EnsureComponent it
/// stores no values; chain Add to provide an explicit initializer.
/// </summary>
/// <typeparam name="TComponentSet">The component set to take types from — typically a queryable.</typeparam>
/// <typeparam name="TInnerBuilder">The wrapped builder type.</typeparam>
public readonly struct EnsureComponentSet<TComponentSet, TInnerBuilder> : IComponentsBuilder
    where TComponentSet : IComponentSet, allows ref struct
    where TInnerBuilder : unmanaged, IComponentsBuilder
{
    /// <summary>The inner builder that this wraps.</summary>
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
        InnerBuilder.WriteComponents(chunkManager, layout, chunkHandle, indexInChunk);
        // This wrapper preserves inner-builder writes. Chain Add to supply an explicit value
        // without duplicating the component type in the mask.
    }

    /// <inheritdoc cref="EntityBuilder.EnsureFrom{TComponentSet}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EnsureComponentSet<TOtherComponentSet, EnsureComponentSet<TComponentSet, TInnerBuilder>> EnsureFrom<TOtherComponentSet>()
        where TOtherComponentSet : IComponentSet, allows ref struct
        => new() { InnerBuilder = this };
}

/// <summary>Extension providing fluent Add and Ensure methods for component builders.</summary>
public static class ComponentsBuilderExtensions
{
    extension<TBuilder>(TBuilder builder)
        where TBuilder : unmanaged, IComponentsBuilder
    {
        /// <summary>Adds a component to the entity being built.</summary>
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
        /// Includes a component type without writing its value.
        /// Use Add to supply an explicit initializer.
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
