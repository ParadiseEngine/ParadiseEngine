using System.Numerics;

namespace Paradise.Physics;

/// <summary>Stores caller-owned sphere position, linear/angular velocity and material properties.</summary>
/// <remarks>Defaults are undamped, frictionless, plastic and non-spinning; set restitution,
/// friction and damping explicitly.</remarks>
public struct DynamicSphere
{
    public Vector3 Position;
    public Vector3 Velocity;

    /// <summary>Angular velocity in rad/s, coupled to linear motion by contact friction.</summary>
    /// <remarks>Y is sidespin; horizontal components provide top/back-spin.</remarks>
    public Vector3 AngularVelocity;

    public float Radius;
    public float Mass;

    /// <summary>Per-second linear damping applied during integration: v *= max(0, 1 − damping·dt).</summary>
    public float LinearDamping;

    /// <summary>Per-second angular damping (spin/rolling resistance): ω *= max(0, 1 − damping·dt).</summary>
    public float AngularDamping;

    /// <summary>Bounce factor for sphere ↔ sphere contacts; a pair bounces with the average of
    /// both spheres' values (0 = plastic, 1 = elastic).</summary>
    public float Restitution;

    /// <summary>Coulomb friction coefficient, limiting tangential impulse to μ times the normal impulse.</summary>
    /// <remarks>Zero prevents spin transfer; a sphere's central normal impulse creates no torque.</remarks>
    public float Friction;

    /// <summary>OUTPUT: impulse magnitude accumulated over this sphere's pairwise collisions
    /// during the last <c>RigidSphereDynamics.Step</c> (zeroed at step start). Game
    /// code reads it for feedback — hit flashes, collision audio intensity.</summary>
    public float ContactImpulse;

    /// <summary>Inverse mass; 0 (unset mass) is treated as unit mass by the solver.</summary>
    public readonly float InverseMass => Mass > 0f ? 1f / Mass : 1f;

    /// <summary>Inverse moment of inertia for a SOLID sphere: I = (2/5)·m·r², isotropic — so I⁻¹
    /// is a scalar and there is no gyroscopic (ω×Iω) term. Zero radius/mass ⇒ 0 (no angular response).</summary>
    public readonly float InverseInertia
    {
        get
        {
            float m = Mass > 0f ? Mass : 1f;
            return Radius > 0f ? 5f / (2f * m * Radius * Radius) : 0f;
        }
    }
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

/// <summary>Tuning for <c>RigidSphereDynamics.Step</c>.</summary>
public struct SphereDynamicsSettings
{
    /// <summary>Gravity acceleration (m/s²), applied to every sphere each step. Default points −Y.</summary>
    public Vector3 Gravity;

    /// <summary>Linear speeds below this settle to rest when the sphere is supported (m/s).</summary>
    public float MinSpeed;

    /// <summary>Angular speeds below this settle to rest when supported (rad/s).</summary>
    public float MinAngularSpeed;

    /// <summary>Bounce factor for sphere ↔ static contacts (0 = no bounce, 1 = elastic).
    /// Sphere ↔ sphere bounce is per-body: <see cref="DynamicSphere.Restitution"/>.</summary>
    public float StaticRestitution;

    /// <summary>Static-contact friction, combined with the sphere coefficient by geometric mean.</summary>
    /// <remarks>A sphere's default zero friction disables this too; set both coefficients for friction.</remarks>
    public float StaticFriction;

    /// <summary>Scale applied to a kinematic pusher's velocity when injected into a sphere.</summary>
    public float PushStrength;

    /// <summary>Clearance kept between surfaces (meters).</summary>
    public float Skin;

    /// <summary>Contact-resolution passes per step.</summary>
    /// <remarks>Distance queries return one static at a time, so corners may need several passes.</remarks>
    public int SolverIterations;

    /// <summary>Filter used for sphere-vs-static casts and depenetration queries. Must include the
    /// ground/floor now that gravity rests spheres on it (not just walls).</summary>
    public CollisionFilter StaticFilter;

    public static SphereDynamicsSettings Default => new()
    {
        Gravity = new Vector3(0f, -9.81f, 0f),
        MinSpeed = 0.01f,
        MinAngularSpeed = 0.05f,
        StaticRestitution = 0.4f,
        StaticFriction = 0.2f,
        PushStrength = 1.2f,
        Skin = 0.02f,
        SolverIterations = 4,
        StaticFilter = CollisionFilter.Default,
    };
}
