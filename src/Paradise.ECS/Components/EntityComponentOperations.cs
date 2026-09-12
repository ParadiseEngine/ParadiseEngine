using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>Implements generated unmanaged value operations, including components with no stored bytes.</summary>
public static class EntityComponentOperations
{
    /// <summary>Reads a component value or returns the default value of a present marker component.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Get<T>(IWorld world, Entity entity) where T : unmanaged, IComponent
    {
        ArgumentNullException.ThrowIfNull(world);
        if (T.Size != 0)
            return world.GetComponent<T>(entity);
        RequireMarker<T>(world, entity);
        return default;
    }

    /// <summary>Replaces an existing component value, validating presence for marker components.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set<T>(IWorld world, Entity entity, T value) where T : unmanaged, IComponent
    {
        ArgumentNullException.ThrowIfNull(world);
        if (T.Size != 0)
        {
            world.GetComponent<T>(entity) = value;
            return;
        }
        // Empty structs occupy a CLR byte, but markers own no bytes in chunk storage.
        RequireMarker<T>(world, entity);
    }

    /// <summary>Returns component presence and copies its value when it owns storage.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGet<T>(IWorld world, Entity entity, out T value) where T : unmanaged, IComponent
    {
        ArgumentNullException.ThrowIfNull(world);
        if (T.Size != 0)
            return world.TryGetComponent(entity, out value);
        value = default;
        return !entity.IsPlaceholder && world.HasComponent<T>(entity);
    }

    private static void RequireMarker<T>(IWorld world, Entity entity) where T : unmanaged, IComponent
    {
        if (entity.IsPlaceholder || !world.HasComponent<T>(entity))
            throw new InvalidOperationException($"Entity {entity} does not have component {typeof(T).Name}.");
    }
}
