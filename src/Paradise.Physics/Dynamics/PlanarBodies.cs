using System.Numerics;

namespace Paradise.Physics;

/// <summary>
/// Mutable state of a dynamic sphere, owned by the caller (e.g. ECS components in a game).
/// The library never stores it — every step is a pure function over caller-owned spans.
/// </summary>
public struct DynamicSphere
{
    public Vector3 Position;
    public Vector3 Velocity;
    public float Radius;
    public float Mass;
}

/// <summary>A kinematic (infinite-mass) capsule pusher — e.g. a player character. It displaces
/// dynamic spheres but is never displaced by them.</summary>
public struct KinematicCapsule
{
    /// <summary>Capsule center (Y-aligned).</summary>
    public Vector3 Position;

    /// <summary>Current velocity, used to inject push impulses into overlapped spheres.</summary>
    public Vector3 Velocity;

    public float Radius;
    public float HalfLength;
}

/// <summary>Tuning for <see cref="PlanarSphereDynamics.Step"/>.</summary>
public struct PlanarDynamicsSettings
{
    /// <summary>Per-second linear damping: v *= max(0, 1 − damping·dt).</summary>
    public float LinearDamping;

    /// <summary>Speeds below this snap to rest (m/s).</summary>
    public float MinSpeed;

    /// <summary>Bounce factor for sphere ↔ static contacts (0 = slide, 1 = elastic).</summary>
    public float StaticRestitution;

    /// <summary>Bounce factor for sphere ↔ sphere contacts.</summary>
    public float DynamicRestitution;

    /// <summary>Scale applied to a kinematic pusher's velocity when injected into a sphere.</summary>
    public float PushStrength;

    /// <summary>Clearance kept between surfaces (meters).</summary>
    public float Skin;

    /// <summary>Filter used for sphere-vs-static casts and depenetration queries.</summary>
    public CollisionFilter StaticFilter;

    public static PlanarDynamicsSettings Default => new()
    {
        LinearDamping = 1.5f,
        MinSpeed = 0.005f,
        StaticRestitution = 0.4f,
        DynamicRestitution = 0.6f,
        PushStrength = 1.2f,
        Skin = 0.02f,
        StaticFilter = CollisionFilter.Default,
    };
}
