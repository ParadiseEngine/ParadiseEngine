namespace Paradise.ECS;

/// <summary>Read-only slot access; reference-policy objects still require owner-defined mutation discipline.</summary>
public readonly struct ReadOnlyManagedLookup<T>(IManagedWorld world) where T : class, IManagedComponent
{
    public T? this[Entity entity] => world.GetManaged<T>(entity);
    public bool Has(Entity entity) => world.HasManaged<T>(entity);
    public bool TryGet(Entity entity, out T? value) => world.TryGetManaged(entity, out value);
}

/// <summary>Reads and replaces existing managed components without making structural changes.</summary>
public readonly struct ManagedLookup<T>(IManagedWorld world) where T : class, IManagedComponent
{
    public T? this[Entity entity]
    {
        get => world.GetManaged<T>(entity);
        set => world.SetManaged(entity, value);
    }

    public bool Has(Entity entity) => world.HasManaged<T>(entity);
    public bool TryGet(Entity entity, out T? value) => world.TryGetManaged(entity, out value);
    public void Set(Entity entity, T? value) => world.SetManaged(entity, value);
    public bool TrySet(Entity entity, T? value)
    {
        if (!world.HasManaged<T>(entity))
            return false;
        world.SetManaged(entity, value);
        return true;
    }
}

/// <summary>Requires archetypal managed-component presence, including a null component value.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class WithManagedAttribute<T> : Attribute where T : class, IManagedComponent;

/// <summary>Excludes archetypal managed-component presence.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class WithoutManagedAttribute<T> : Attribute where T : class, IManagedComponent;

/// <summary>Accepts at least one of the managed components named by these attributes.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class WithManagedAnyAttribute<T> : Attribute where T : class, IManagedComponent;
