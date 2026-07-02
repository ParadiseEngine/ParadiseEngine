namespace Paradise.Physics;

/// <summary>
/// Layer-based collision filtering (Unity Physics semantics): two colliders interact when each
/// belongs to a layer the other collides with. <see cref="GroupIndex"/> overrides the masks —
/// equal positive values force collision, equal negative values suppress it.
/// </summary>
public struct CollisionFilter : IEquatable<CollisionFilter>
{
    public uint BelongsTo;
    public uint CollidesWith;
    public int GroupIndex;

    /// <summary>Collides with everything.</summary>
    public static readonly CollisionFilter Default = new() { BelongsTo = ~0u, CollidesWith = ~0u, GroupIndex = 0 };

    public static bool IsCollisionEnabled(in CollisionFilter a, in CollisionFilter b)
    {
        if (a.GroupIndex != 0 && a.GroupIndex == b.GroupIndex) return a.GroupIndex > 0;
        return (a.BelongsTo & b.CollidesWith) != 0 && (b.BelongsTo & a.CollidesWith) != 0;
    }

    public readonly bool Equals(CollisionFilter other)
        => BelongsTo == other.BelongsTo && CollidesWith == other.CollidesWith && GroupIndex == other.GroupIndex;

    public override readonly bool Equals(object? obj) => obj is CollisionFilter other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(BelongsTo, CollidesWith, GroupIndex);

    public static bool operator ==(CollisionFilter left, CollisionFilter right) => left.Equals(right);

    public static bool operator !=(CollisionFilter left, CollisionFilter right) => !left.Equals(right);
}
