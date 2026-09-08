namespace Paradise.ECS;

/// <summary>A compile-time component set used to build entities from queryable requirements.</summary>
/// <remarks>
/// Generated queryables contribute only <c>[With]</c> components. <c>[Without]</c> forbids components;
/// <c>[WithAny]</c> and <c>[Optional]</c> do not identify a required set.
/// </remarks>
public interface IComponentSet
{
    /// <summary>
    /// Adds this set's component type IDs to <paramref name="mask"/>, leaving any bits already
    /// set alone — so several sets compose into one archetype by union.
    /// </summary>
    /// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
    /// <param name="mask">The mask to add component types to.</param>
    static abstract void CollectComponentTypes<TMask>(ref TMask mask)
        where TMask : unmanaged, IBitSet<TMask>;
}
