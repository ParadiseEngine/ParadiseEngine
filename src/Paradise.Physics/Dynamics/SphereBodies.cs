using System.Numerics;

namespace Paradise.Physics;

/// <summary>
/// Mutable state of a dynamic sphere, owned by the caller (e.g. ECS components in a game).
/// The library never stores it — every step is a pure function over caller-owned spans.
/// This is a FULL 3D rigid body: it carries linear AND angular velocity, feels gravity, and
/// resolves contacts with Coulomb friction at the contact point (lever arm). All fields
/// zero-initialize; an unset sphere is undamped, frictionless, plastic, and non-spinning — set
/// the material params (<see cref="Restitution"/>, <see cref="Friction"/>, damping) explicitly.
/// (Type name is historical — the model is no longer planar; Y and angular state are live.)
/// </summary>
public struct DynamicSphere
{
    public Vector3 Position;
    public Vector3 Velocity;

    /// <summary>Angular velocity (rad/s), full 3D. Sidespin ("english") is the Y component; a
    /// horizontal-axis component is top/back-spin (follow/draw). Coupled to linear motion only
    /// through the friction impulse at contacts — draw, follow, throw and rolling all emerge.</summary>
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

    /// <summary>Coulomb friction coefficient μ. The tangential contact impulse is clamped to
    /// μ·|normal impulse|; this is the ONLY coupling between spin and linear motion (a central
    /// normal impulse produces no torque on a sphere). 0 = frictionless (spin never transfers).</summary>
    public float Friction;

    /// <summary>OUTPUT: impulse magnitude accumulated over this sphere's pairwise collisions
    /// during the last <see cref="RigidSphereDynamics.Step"/> (zeroed at step start). Game
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

/// <summary>Tuning for <see cref="RigidSphereDynamics.Step"/>.</summary>
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

    /// <summary>Coulomb friction coefficient for sphere ↔ static contacts (cushions, cloth).
    /// Combined with the sphere's own <see cref="DynamicSphere.Friction"/> via the GEOMETRIC MEAN —
    /// so a sphere that leaves <see cref="DynamicSphere.Friction"/> at its 0 default cancels this
    /// entirely (√(0·x)=0, frictionless): set per-sphere Friction when you want static friction.</summary>
    public float StaticFriction;

    /// <summary>Scale applied to a kinematic pusher's velocity when injected into a sphere.</summary>
    public float PushStrength;

    /// <summary>Clearance kept between surfaces (meters).</summary>
    public float Skin;

    /// <summary>Contact-resolution iterations per step (re-query + resolve). A sphere resting in a
    /// corner touches several statics at once but <c>CalculateDistance</c> returns one at a time,
    /// so a few passes are needed to settle. 1 is fine for open-table motion.</summary>
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
