namespace Paradise.ECS;

/// <summary>Declares a sealed partial class stored through an archetypal managed slot.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ManagedComponentAttribute(string? guid = null) : Attribute
{
    public string? Guid { get; } = guid;
    public int Id { get; set; } = -1;
    public ManagedSnapshot Snapshot { get; set; } = ManagedSnapshot.Reference;
}

/// <summary>Controls how a managed component participates in a world snapshot.</summary>
public enum ManagedSnapshot
{
    /// <summary>Shares the object, whose contents are observed at read time rather than copy time.</summary>
    Reference,
    /// <summary>Clones each non-null object, allocating according to the component's clone method.</summary>
    Clone,
    /// <summary>Preserves archetypal presence and handles while resolving objects to null.</summary>
    Skip
}

/// <summary>Identifies the generated unmanaged slot for a managed component.</summary>
public interface IManagedComponent
{
    static abstract ComponentId SlotTypeId { get; }
    static abstract Guid Guid { get; }
}

/// <summary>Supplies an AOT-safe snapshot clone for a managed component.</summary>
public interface IManagedClone<TSelf> where TSelf : class
{
    static abstract TSelf Clone(TSelf source);
}

/// <summary>Provides managed component access without exposing the underlying slot handles.</summary>
public interface IManagedWorld
{
    void AddManaged<T>(Entity entity, T? value) where T : class, IManagedComponent;
    T? GetManaged<T>(Entity entity) where T : class, IManagedComponent;
    void SetManaged<T>(Entity entity, T? value) where T : class, IManagedComponent;
    bool TryGetManaged<T>(Entity entity, out T? value) where T : class, IManagedComponent;
    bool HasManaged<T>(Entity entity) where T : class, IManagedComponent;
    void RemoveManaged<T>(Entity entity) where T : class, IManagedComponent;
}
