namespace Paradise.ECS;

/// <summary>Provides generated static presence operations for components, tags, and managed components.</summary>
public interface IEntityComponent
{
    /// <summary>Adds a default component value or tag presence to an entity.</summary>
    static abstract void Add(IWorld world, Entity entity);

    /// <summary>Returns whether an entity has this component or tag.</summary>
    static abstract bool Has(IWorld world, Entity entity);

    /// <summary>Removes this component or tag from an entity.</summary>
    static abstract void Remove(IWorld world, Entity entity);
}

/// <summary>Provides generated static value operations for unmanaged and managed components.</summary>
/// <remarks>Managed values may be null while the component remains present; tags only implement presence operations.</remarks>
/// <typeparam name="TSelf">The component type.</typeparam>
public interface IEntityComponent<TSelf> : IEntityComponent
    where TSelf : IEntityComponent<TSelf>
{
    /// <summary>Adds a component with the supplied value.</summary>
    static abstract void Add(IWorld world, Entity entity, TSelf? value);

    /// <summary>Reads a component value, copying unmanaged data or returning the managed object.</summary>
    static abstract TSelf? Get(IWorld world, Entity entity);

    /// <summary>Replaces an existing component value.</summary>
    static abstract void Set(IWorld world, Entity entity, TSelf? value);

    /// <summary>Returns component presence and its value independently of managed null values.</summary>
    static abstract bool TryGet(IWorld world, Entity entity, out TSelf? value);
}
