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

    /// <summary>Enables all layers; the other collider's masks still participate in filtering.</summary>
    public static readonly CollisionFilter Default = new() { BelongsTo = ~0u, CollidesWith = ~0u, GroupIndex = 0 };

    /// <summary>The layer bit that marks a body as walkable ground for <see cref="CharacterGround"/> probes.</summary>
    /// <remarks>Bit 31 is reserved for this convention; game layers should use bits 0–30. <see cref="Default"/>
    /// belongs to every layer and is therefore ground; clear this bit from <see cref="BelongsTo"/>
    /// (for example <c>BelongsTo = ~GroundLayer</c>) to keep walls and props out of ground support.</remarks>
    public const uint GroundLayer = 1u << 31;

    /// <summary>Marks a walkable body: belongs only to <see cref="GroundLayer"/> and collides with every layer.</summary>
    /// <remarks>Combine <see cref="GroundLayer"/> with other layer bits instead when the body must also
    /// belong to game layers, such as a crate that blocks movement and can be stood on.</remarks>
    public static readonly CollisionFilter Ground = new() { BelongsTo = GroundLayer, CollidesWith = ~0u, GroupIndex = 0 };

    /// <summary>Query filter that sees only bodies belonging to <see cref="GroundLayer"/>.</summary>
    public static readonly CollisionFilter GroundProbe = new() { BelongsTo = ~0u, CollidesWith = GroundLayer, GroupIndex = 0 };

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
