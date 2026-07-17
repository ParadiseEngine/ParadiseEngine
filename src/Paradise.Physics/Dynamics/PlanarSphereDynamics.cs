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
        => Step(spheres, pushers, statics?.Handle ?? default, settings, deltaSeconds);

    /// <summary>Handle-based overload for use inside ECS systems. An invalid handle means no
    /// statics: spheres integrate unobstructed (pushes and pair impulses still apply).</summary>
    public static void Step(Span<DynamicSphere> spheres, ReadOnlySpan<KinematicCapsule> pushers,
        CollisionWorldHandle statics, in PlanarDynamicsSettings settings, float deltaSeconds)
    {
        for (int i = 0; i < spheres.Length; i++)
        {
            spheres[i].ContactImpulse = 0f; // per-step output; see DynamicSphere.ContactImpulse
        }
        PushFromKinematics(spheres, pushers, statics, settings);
        Integrate(spheres, statics, settings, deltaSeconds);
        ResolvePairs(spheres, statics, settings);
        DepenetrateFromStatics(spheres, statics, settings);
    }

    /// <summary>Clamp a proposed move onto supported ground when the settings require it.
    /// Applied at EVERY position-mutating site (push, integrate — including wall-bounce
    /// deflections, pair resolution, static depenetration) so the "from is supported" invariant
    /// of <see cref="PlanarGroundSupport.Clamp"/> holds inductively across ticks.</summary>
    private static Vector3 ClampToSupport(CollisionWorldHandle statics, in PlanarDynamicsSettings settings,
        Vector3 from, Vector3 to)
    {
        if (!settings.RequireSupport || !statics.IsValid) return to;
        return PlanarGroundSupport.Clamp(statics, settings.SupportFilter, from, to, settings.SupportProbeDepth);
    }

    /// <summary>Kinematic pushers displace overlapped spheres and drive their contact-normal
    /// velocity up to pusherSpeed·PushStrength (a stable "carry along" rather than an
    /// accumulating impulse). Pushers are infinite-mass and never move.</summary>
    private static void PushFromKinematics(Span<DynamicSphere> spheres, ReadOnlySpan<KinematicCapsule> pushers,
        CollisionWorldHandle statics, in PlanarDynamicsSettings settings)
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
                sphere.Position = ClampToSupport(statics, settings,
                    sphere.Position, sphere.Position + normal * depenetration); // horizontal (normal.Y == 0)

                float targetSpeed = MathF.Max(0f, Vector3.Dot(pusher.Velocity, normal)) * settings.PushStrength;
                float currentSpeed = Vector3.Dot(sphere.Velocity, normal);
                if (currentSpeed < targetSpeed)
                {
                    sphere.Velocity += normal * (targetSpeed - currentSpeed);
                }
            }
        }
    }

    private static void Integrate(Span<DynamicSphere> spheres, CollisionWorldHandle statics,
        in PlanarDynamicsSettings settings, float deltaSeconds)
    {
        for (int i = 0; i < spheres.Length; i++)
        {
            ref DynamicSphere sphere = ref spheres[i];
            Vector3 velocity = sphere.Velocity;
            velocity.Y = 0f;
            velocity *= MathF.Max(0f, 1f - sphere.LinearDamping * deltaSeconds);
            if (velocity.LengthSquared() < settings.MinSpeed * settings.MinSpeed)
            {
                sphere.Velocity = Vector3.Zero;
                continue;
            }

            Vector3 remaining = velocity * deltaSeconds;
            if (!statics.IsValid)
            {
                sphere.Position += remaining;
                sphere.Velocity = velocity;
                continue;
            }
            if (settings.RequireSupport)
            {
                Vector3 clamped = ClampToSupport(statics, settings, sphere.Position, sphere.Position + remaining);
                remaining = clamped - sphere.Position;
                // Kill the velocity component the edge rejected so the sphere rests at the rim
                // instead of grinding against it forever.
                if (remaining.LengthSquared() < 1e-12f)
                {
                    sphere.Velocity = Vector3.Zero;
                    continue;
                }
                velocity = remaining / deltaSeconds;
            }

            Collider ball = Collider.CreateSphere(sphere.Radius, settings.StaticFilter);
            Vector3 preIntegrate = sphere.Position;
            Vector3 position = sphere.Position;
            for (int iteration = 0; iteration < MaxSlideIterations && remaining.LengthSquared() > MinMoveSq; iteration++)
            {
                float length = remaining.Length();
                Vector3 direction = remaining / length;
                // The cast is padded by Skin: the resolver keeps bodies Skin away from statics,
                // so a cast of exactly this tick's displacement can never reach a wall from the
                // clearance band when the tick's move is shorter than Skin (speed < Skin/dt) —
                // the ball would creep in, get pushed back out by the depenetration pass, and
                // grind against the wall forever with its velocity never reflected.
                var input = new ColliderCastInput
                {
                    Collider = ball,
                    Orientation = Quaternion.Identity,
                    Start = position,
                    End = position + direction * (length + settings.Skin),
                };
                if (!statics.CastCollider(input, out ColliderCastHit hit))
                {
                    position += remaining;
                    break;
                }

                // Distance to surface contact along the padded cast; stop Skin short of it,
                // never moving farther than this tick's actual displacement.
                float contact = (length + settings.Skin) * hit.Fraction;
                float travel = MathF.Min(contact - settings.Skin, length);
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
                    ApplyRailEnglish(ref velocity, ref sphere.SpinY, normal, settings);
                }

                Vector3 rest = direction * (length - MathF.Max(travel, 0f));
                float restInto = Vector3.Dot(rest, normal);
                if (restInto < 0f)
                {
                    rest -= (1f + settings.StaticRestitution) * restInto * normal;
                }
                remaining = rest;
            }

            // The pre-loop clamp only bounds the straight-line move; a wall bounce can deflect
            // the remaining displacement toward an open edge within the same step, so the final
            // position is clamped again against the (supported) pre-integrate position.
            sphere.Position = ClampToSupport(statics, settings, preIntegrate,
                new Vector3(position.X, sphere.Position.Y, position.Z));
            sphere.Velocity = velocity;
        }
    }

    /// <summary>Pairwise sphere-sphere: split the depenetration half/half along the horizontal
    /// center axis and exchange the standard 1-D collision impulse.</summary>
    private static void ResolvePairs(Span<DynamicSphere> spheres, CollisionWorldHandle statics, in PlanarDynamicsSettings settings)
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
                a.Position = ClampToSupport(statics, settings, a.Position, a.Position - normal * (penetration * 0.5f));
                b.Position = ClampToSupport(statics, settings, b.Position, b.Position + normal * (penetration * 0.5f));

                float massA = a.Mass > 0f ? a.Mass : 1f;
                float massB = b.Mass > 0f ? b.Mass : 1f;
                float approaching = Vector3.Dot(b.Velocity - a.Velocity, normal);
                if (approaching >= 0f) continue; // separating already

                float restitution = 0.5f * (a.Restitution + b.Restitution);
                float impulse = -(1f + restitution) * approaching / (1f / massA + 1f / massB);
                a.Velocity -= normal * (impulse / massA);
                b.Velocity += normal * (impulse / massB);
                a.ContactImpulse += impulse;
                b.ContactImpulse += impulse;
            }
        }
    }

    /// <summary>Second static pass: pair resolution can shove a sphere into a wall; push it back
    /// out to skin clearance. Also the bounce of last resort: a sphere inside the skin band whose
    /// velocity still points INTO the surface never got its cast bounce — an oblique contact's
    /// along-path gap grows as 1/sin(θ), so no finite cast padding catches every slow shallow
    /// approach; without the reflection here such a ball slides along the wall (tangential motion
    /// preserved, normal motion cancelled by the push-out) instead of rebounding.</summary>
    private static void DepenetrateFromStatics(Span<DynamicSphere> spheres, CollisionWorldHandle statics,
        in PlanarDynamicsSettings settings)
    {
        if (!statics.IsValid) return;

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

            sphere.Position = ClampToSupport(statics, settings,
                sphere.Position, sphere.Position + normal * (settings.Skin - hit.Distance));

            // Reflect only an into-surface velocity (a cast bounce this step already turned
            // the sphere away, and dot ≥ 0 then keeps this from double-bouncing it).
            float velocityInto = Vector3.Dot(sphere.Velocity, normal);
            if (velocityInto < 0f)
            {
                sphere.Velocity -= (1f + settings.StaticRestitution) * velocityInto * normal;
                // English here too: slow shallow banking rail shots reflect ONLY in this pass
                // (their oblique along-path gap outruns any cast padding), and that regime is
                // exactly where english matters most. Same into-surface guard as the cast bounce,
                // so when the cast path already reflected this wall (velocityInto ≥ 0 here) the
                // spin is neither re-kicked nor double-bled.
                ApplyRailEnglish(ref sphere.Velocity, ref sphere.SpinY, normal, settings);
            }
        }
    }

    /// <summary>Sidespin ("english") at a cushion contact: bend the rebound along the rail
    /// tangent by the sphere's <see cref="DynamicSphere.SpinY"/>, then bleed the spin. Stateless —
    /// SpinY is a caller-owned span slot the library only reads and writes; nothing is stored
    /// between steps. Called at both rebound sites (cast bounce + depenetration fallback), each
    /// guarded by its own into-surface test so a single contact is kicked and bled exactly once.
    /// No-op when english is off (<see cref="PlanarDynamicsSettings.RailEnglish"/> 0).</summary>
    private static void ApplyRailEnglish(ref Vector3 velocity, ref float spinY, Vector3 normal,
        in PlanarDynamicsSettings settings)
    {
        if (settings.RailEnglish == 0f || spinY == 0f) return;
        Vector3 tangent = Vector3.Cross(Vector3.UnitY, normal); // horizontal unit (UnitY ⟂ horizontal normal)
        velocity += tangent * (spinY * settings.RailEnglish);
        spinY *= settings.RailSpinLoss;
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
