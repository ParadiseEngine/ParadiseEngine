namespace Paradise.ECS;

/// <summary>
/// A compile-time set of component types that an entity can be built from.
///
/// Implemented automatically by every <c>[Queryable]</c> (the generator emits it from the
/// queryable's <c>[With]</c> components), which is the point: the archetype an entity is created
/// with stops being a hand-maintained list that has to agree with what the systems query for.
/// Add a component to a queryable and every entity built from it gains it, instead of the query
/// silently matching nothing.
///
/// Only <c>[With]</c> components are contributed. <c>[Without]</c> is excluded by construction —
/// an entity carrying one could never match. <c>[WithAny]</c> and <c>[Optional]</c> are excluded
/// because neither names a single required component: the set would be a guess.
/// </summary>
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
