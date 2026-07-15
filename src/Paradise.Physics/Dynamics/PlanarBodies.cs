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

    /// <summary>Per-second linear damping applied during integration: v *= max(0, 1 − damping·dt).</summary>
    public float LinearDamping;

    /// <summary>Bounce factor for sphere ↔ sphere contacts; a pair bounces with the average of
    /// both spheres' values (0 = plastic, 1 = elastic).</summary>
    public float Restitution;

    /// <summary>OUTPUT: impulse magnitude accumulated over this sphere's pairwise collisions
    /// during the last <see cref="PlanarSphereDynamics.Step"/> (zeroed at step start). Game
    /// code reads it for feedback — hit flashes, collision audio intensity.</summary>
    public float ContactImpulse;
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
    /// <summary>Speeds below this snap to rest (m/s).</summary>
    public float MinSpeed;

    /// <summary>Bounce factor for sphere ↔ static contacts (0 = slide, 1 = elastic).
    /// Sphere ↔ sphere bounce is per-body: <see cref="DynamicSphere.Restitution"/>.</summary>
    public float StaticRestitution;

    /// <summary>Scale applied to a kinematic pusher's velocity when injected into a sphere.</summary>
    public float PushStrength;

    /// <summary>Clearance kept between surfaces (meters).</summary>
    public float Skin;

    /// <summary>Filter used for sphere-vs-static casts and depenetration queries.</summary>
    public CollisionFilter StaticFilter;

    /// <summary>When true, spheres are kept on supported ground: every push/integrate move is
    /// clamped so a downward ray (<see cref="SupportFilter"/>, <see cref="SupportProbeDepth"/>)
    /// from the new position still hits support geometry (see <see cref="PlanarGroundSupport"/>).</summary>
    public bool RequireSupport;

    /// <summary>Filter for the downward support probe (typically the floor layer only).</summary>
    public CollisionFilter SupportFilter;

    /// <summary>How far below a body's center the support probe reaches (meters).</summary>
    public float SupportProbeDepth;

    public static PlanarDynamicsSettings Default => new()
    {
        MinSpeed = 0.005f,
        StaticRestitution = 0.4f,
        PushStrength = 1.2f,
        Skin = 0.02f,
        StaticFilter = CollisionFilter.Default,
        RequireSupport = false,
        SupportFilter = CollisionFilter.Default,
        SupportProbeDepth = 10f,
    };
}
