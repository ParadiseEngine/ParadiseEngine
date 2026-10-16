namespace Paradise.ECS;

/// <summary>A compile-time component set used to build entities from queryable requirements.</summary>
/// <remarks>
/// Generated queryables contribute required component slots, including <c>[WithManaged]</c>, and
/// EntityTags storage for tag filters. Excluded, any-of and optional claims contribute no required types;
/// this operation sets neither component values nor tag bits.
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
