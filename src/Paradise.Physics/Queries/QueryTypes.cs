using System.Numerics;

namespace Paradise.Physics;

public struct RaycastInput
{
    public Vector3 Start;
    public Vector3 End;
    public CollisionFilter Filter;
}

public struct RaycastHit
{
    /// <summary>Parametric distance along Start → End, in [0, 1].</summary>
    public float Fraction;

    /// <summary>World-space surface point. A ray starting inside a collider reports
    /// Fraction = 0, Position = Start and SurfaceNormal = -normalize(End - Start).</summary>
    public Vector3 Position;

    /// <summary>Unit normal of the hit surface, pointing back toward the ray origin side.</summary>
    public Vector3 SurfaceNormal;

    public int BodyIndex;
}

public struct ColliderCastInput
{
    /// <summary>The cast shape. Its <see cref="Collider.Filter"/> is matched against target filters.</summary>
    public Collider Collider;

    /// <summary>Fixed orientation of the cast shape (linear sweep — no rotation over the cast).</summary>
    public Quaternion Orientation;

    /// <summary>Path of the collider origin.</summary>
    public Vector3 Start;

    public Vector3 End;
}

public struct ColliderCastHit
{
    /// <summary>Parametric distance along Start → End, in [0, 1]. A cast that starts in
    /// contact or overlapping reports Fraction = 0 with the depenetration direction as normal.</summary>
    public float Fraction;

    /// <summary>World-space contact point on the hit body's surface.</summary>
    public Vector3 Position;

    /// <summary>Unit normal of the hit surface, pointing back toward the caster.</summary>
    public Vector3 SurfaceNormal;

    public int BodyIndex;
}

public struct ColliderDistanceInput
{
    public Collider Collider;
    public RigidTransform Transform;

    /// <summary>Bodies farther than this are ignored.</summary>
    public float MaxDistance;
}

public struct DistanceHit
{
    /// <summary>Separation between the surfaces; negative = penetration depth
    /// (approximate once the core shapes themselves overlap).</summary>
    public float Distance;

    /// <summary>Closest (or deepest) point on the hit body's surface.</summary>
    public Vector3 Position;

    /// <summary>Unit direction from the hit body toward the query collider.</summary>
    public Vector3 SurfaceNormal;

    public int BodyIndex;
}
