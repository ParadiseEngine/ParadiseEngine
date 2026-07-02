using System.Numerics;

namespace Paradise.Physics;

/// <summary>
/// Stateless planar dynamics for spheres (the bank-heist resolver pipeline on top of the query
/// library): kinematic push → damp/integrate with cast-and-bounce against statics → pairwise
/// sphere impulses → static depenetration pass. Mutates the caller's spans in place; the library
/// keeps no state, so the step is a pure function of its inputs and deterministic for a fixed
/// span order. Planar contract: Y is never modified; all contact normals are flattened to XZ.
/// </summary>
public static class PlanarSphereDynamics
{
    private const int MaxSlideIterations = 4;
    private const float MinMoveSq = 1e-10f;
    private const float MinHorizontalNormal = 1e-3f;

    public static void Step(Span<DynamicSphere> spheres, ReadOnlySpan<KinematicCapsule> pushers,
        CollisionWorld? statics, in PlanarDynamicsSettings settings, float deltaSeconds)
    {
        PushFromKinematics(spheres, pushers, settings);
        Integrate(spheres, statics, settings, deltaSeconds);
        ResolvePairs(spheres, settings);
        DepenetrateFromStatics(spheres, statics, settings);
    }

    /// <summary>Kinematic pushers displace overlapped spheres and drive their contact-normal
    /// velocity up to pusherSpeed·PushStrength (a stable "carry along" rather than an
    /// accumulating impulse). Pushers are infinite-mass and never move.</summary>
    private static void PushFromKinematics(Span<DynamicSphere> spheres, ReadOnlySpan<KinematicCapsule> pushers,
        in PlanarDynamicsSettings settings)
    {
        foreach (ref readonly KinematicCapsule pusher in pushers)
        {
            Collider capsule = Collider.CreateCapsule(pusher.Radius, pusher.HalfLength);
            var capsulePose = new RigidTransform(pusher.Position, Quaternion.Identity);

            for (int i = 0; i < spheres.Length; i++)
            {
                ref DynamicSphere sphere = ref spheres[i];
                Collider ball = Collider.CreateSphere(sphere.Radius);
                ColliderQueries.DistanceBetween(ball, new RigidTransform(sphere.Position, Quaternion.Identity),
                    capsule, capsulePose, out DistanceHit hit);
                if (hit.Distance >= settings.Skin) continue;

                // Normal points from the capsule toward the sphere — the push-out direction.
                if (!TryHorizontal(hit.SurfaceNormal, sphere.Position - pusher.Position, out Vector3 normal)) continue;

                float depenetration = settings.Skin - hit.Distance;
                sphere.Position += normal * depenetration; // horizontal (normal.Y == 0)

                float targetSpeed = MathF.Max(0f, Vector3.Dot(pusher.Velocity, normal)) * settings.PushStrength;
                float currentSpeed = Vector3.Dot(sphere.Velocity, normal);
                if (currentSpeed < targetSpeed)
                {
                    sphere.Velocity += normal * (targetSpeed - currentSpeed);
                }
            }
        }
    }

    private static void Integrate(Span<DynamicSphere> spheres, CollisionWorld? statics,
        in PlanarDynamicsSettings settings, float deltaSeconds)
    {
        for (int i = 0; i < spheres.Length; i++)
        {
            ref DynamicSphere sphere = ref spheres[i];
            Vector3 velocity = sphere.Velocity;
            velocity.Y = 0f;
            velocity *= MathF.Max(0f, 1f - settings.LinearDamping * deltaSeconds);
            if (velocity.LengthSquared() < settings.MinSpeed * settings.MinSpeed)
            {
                sphere.Velocity = Vector3.Zero;
                continue;
            }

            Vector3 remaining = velocity * deltaSeconds;
            if (statics is null)
            {
                sphere.Position += remaining;
                sphere.Velocity = velocity;
                continue;
            }

            Collider ball = Collider.CreateSphere(sphere.Radius, settings.StaticFilter);
            Vector3 position = sphere.Position;
            for (int iteration = 0; iteration < MaxSlideIterations && remaining.LengthSquared() > MinMoveSq; iteration++)
            {
                var input = new ColliderCastInput
                {
                    Collider = ball,
                    Orientation = Quaternion.Identity,
                    Start = position,
                    End = position + remaining,
                };
                if (!statics.CastCollider(input, out ColliderCastHit hit))
                {
                    position += remaining;
                    break;
                }

                float length = remaining.Length();
                Vector3 direction = remaining / length;
                float travel = length * hit.Fraction - settings.Skin;
                if (travel > 0f)
                {
                    position += direction * travel;
                }

                if (!TryHorizontal(hit.SurfaceNormal, -direction, out Vector3 normal))
                {
                    break; // near-vertical contact: planar backstop
                }

                // Bounce: reflect the incoming normal component with restitution, for both the
                // velocity (future ticks) and the remaining displacement (this tick).
                float velocityInto = Vector3.Dot(velocity, normal);
                if (velocityInto < 0f)
                {
                    velocity -= (1f + settings.StaticRestitution) * velocityInto * normal;
                }

                Vector3 rest = remaining * (1f - hit.Fraction);
                float restInto = Vector3.Dot(rest, normal);
                if (restInto < 0f)
                {
                    rest -= (1f + settings.StaticRestitution) * restInto * normal;
                }
                remaining = rest;
            }

            sphere.Position = new Vector3(position.X, sphere.Position.Y, position.Z);
            sphere.Velocity = velocity;
        }
    }

    /// <summary>Pairwise sphere-sphere: split the depenetration half/half along the horizontal
    /// center axis and exchange the standard 1-D collision impulse.</summary>
    private static void ResolvePairs(Span<DynamicSphere> spheres, in PlanarDynamicsSettings settings)
    {
        for (int i = 0; i < spheres.Length; i++)
        {
            for (int j = i + 1; j < spheres.Length; j++)
            {
                ref DynamicSphere a = ref spheres[i];
                ref DynamicSphere b = ref spheres[j];

                var delta = new Vector3(b.Position.X - a.Position.X, 0f, b.Position.Z - a.Position.Z);
                float distance = delta.Length();
                float minDistance = a.Radius + b.Radius;
                if (distance >= minDistance) continue;

                Vector3 normal = distance > 1e-6f ? delta / distance : Vector3.UnitX; // deterministic fallback
                float penetration = minDistance - distance;
                a.Position -= normal * (penetration * 0.5f);
                b.Position += normal * (penetration * 0.5f);

                float massA = a.Mass > 0f ? a.Mass : 1f;
                float massB = b.Mass > 0f ? b.Mass : 1f;
                float approaching = Vector3.Dot(b.Velocity - a.Velocity, normal);
                if (approaching >= 0f) continue; // separating already

                float impulse = -(1f + settings.DynamicRestitution) * approaching / (1f / massA + 1f / massB);
                a.Velocity -= normal * (impulse / massA);
                b.Velocity += normal * (impulse / massB);
            }
        }
    }

    /// <summary>Second static pass: pair resolution can shove a sphere into a wall; push it back
    /// out to skin clearance.</summary>
    private static void DepenetrateFromStatics(Span<DynamicSphere> spheres, CollisionWorld? statics,
        in PlanarDynamicsSettings settings)
    {
        if (statics is null) return;

        for (int i = 0; i < spheres.Length; i++)
        {
            ref DynamicSphere sphere = ref spheres[i];
            var input = new ColliderDistanceInput
            {
                Collider = Collider.CreateSphere(sphere.Radius, settings.StaticFilter),
                Transform = new RigidTransform(sphere.Position, Quaternion.Identity),
                MaxDistance = settings.Skin,
            };
            if (!statics.CalculateDistance(input, out DistanceHit hit)) continue;
            if (hit.Distance >= settings.Skin) continue;
            if (!TryHorizontal(hit.SurfaceNormal, Vector3.UnitX, out Vector3 normal)) continue;

            sphere.Position += normal * (settings.Skin - hit.Distance);
        }
    }

    /// <summary>Flatten a contact normal to the XZ plane; falls back to the (flattened)
    /// alternative direction, then +X, so the result is always a horizontal unit vector unless
    /// both inputs are vertical (returns false → caller skips the planar response).</summary>
    private static bool TryHorizontal(Vector3 normal, Vector3 fallback, out Vector3 horizontal)
    {
        var flat = new Vector3(normal.X, 0f, normal.Z);
        float length = flat.Length();
        if (length >= MinHorizontalNormal)
        {
            horizontal = flat / length;
            return true;
        }

        flat = new Vector3(fallback.X, 0f, fallback.Z);
        length = flat.Length();
        if (length >= MinHorizontalNormal)
        {
            horizontal = flat / length;
            return true;
        }

        horizontal = default;
        return false;
    }
}
