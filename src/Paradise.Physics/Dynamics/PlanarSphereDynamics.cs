using System.Numerics;

namespace Paradise.Physics;

/// <summary>
/// Stateless 3D rigid-body dynamics for spheres (the resolver pipeline on top of the query
/// library): gravity + damping → swept move with cast-and-resolve against statics → pairwise
/// sphere impulses → resting depenetration/support. Every contact applies a NORMAL impulse
/// (central → no torque on a sphere) and a Coulomb FRICTION impulse at the contact point with a
/// lever arm (<c>ω += I⁻¹·(r×j)</c>) — so draw, follow, throw, rolling and english all EMERGE
/// from one solver. Mutates the caller's spans in place; the library keeps no state, so the step
/// is a pure function of its inputs and deterministic for a fixed span order.
///
/// The sphere's ORIENTATION is not owned here (this struct has only angular VELOCITY); the caller
/// integrates the quaternion from <see cref="DynamicSphere.AngularVelocity"/>.
/// (Type name is historical — the model is fully 3D now, not planar.)
/// </summary>
public static class PlanarSphereDynamics
{
    private const int MaxSlideIterations = 4;
    private const float MinMoveSq = 1e-10f;
    private const float Eps = 1e-6f;

    // Below this closing speed a contact is treated as resting (restitution suppressed) so a ball
    // sitting under gravity doesn't jitter/bounce on the felt forever.
    private const float RestitutionSlop = 0.5f;

    // A contact whose normal points sufficiently up counts as "support" (for sleeping).
    private const float SupportNormalY = 0.5f;

    public static void Step(Span<DynamicSphere> spheres, ReadOnlySpan<KinematicCapsule> pushers,
        CollisionWorld? statics, in PlanarDynamicsSettings settings, float deltaSeconds)
        => Step(spheres, pushers, statics?.Handle ?? default, settings, deltaSeconds);

    /// <summary>Handle-based overload for use inside ECS systems. An invalid handle means no
    /// statics: spheres integrate under gravity unobstructed (pushes and pair impulses still apply).</summary>
    public static void Step(Span<DynamicSphere> spheres, ReadOnlySpan<KinematicCapsule> pushers,
        CollisionWorldHandle statics, in PlanarDynamicsSettings settings, float deltaSeconds)
    {
        for (int i = 0; i < spheres.Length; i++)
        {
            spheres[i].ContactImpulse = 0f;
        }
        PushFromKinematics(spheres, pushers, settings);
        for (int i = 0; i < spheres.Length; i++)
        {
            Integrate(ref spheres[i], statics, settings, deltaSeconds);
        }
        ResolvePairs(spheres, settings);

        int iterations = Math.Max(1, settings.SolverIterations);
        for (int iter = 0; iter < iterations; iter++)
        {
            for (int i = 0; i < spheres.Length; i++)
            {
                DepenetrateAndSupport(ref spheres[i], statics, settings, out bool supported);
                if (iter == iterations - 1)
                {
                    Settle(ref spheres[i], settings, supported);
                }
            }
        }
    }

    /// <summary>Kinematic pushers displace overlapped spheres and drive their contact-normal
    /// velocity up to pusherSpeed·PushStrength (a stable carry-along). Horizontal only — pushers
    /// are Y-aligned capsules and never lift a ball.</summary>
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

                Vector3 normal = Horizontal(sphere.Position - pusher.Position);
                if (normal == Vector3.Zero) continue;

                float depenetration = settings.Skin - hit.Distance;
                sphere.Position += normal * depenetration;

                float targetSpeed = MathF.Max(0f, Vector3.Dot(pusher.Velocity, normal)) * settings.PushStrength;
                float currentSpeed = Vector3.Dot(sphere.Velocity, normal);
                if (currentSpeed < targetSpeed)
                {
                    sphere.Velocity += normal * (targetSpeed - currentSpeed);
                }
            }
        }
    }

    /// <summary>Gravity + damping, then a swept cast-and-resolve move against statics: at each hit
    /// the normal+friction impulse is applied and the remaining displacement slides along the
    /// surface (so a ball banks around a cushion within one tick).</summary>
    private static void Integrate(ref DynamicSphere sphere, CollisionWorldHandle statics,
        in PlanarDynamicsSettings settings, float dt)
    {
        Vector3 velocity = sphere.Velocity + settings.Gravity * dt;
        velocity *= MathF.Max(0f, 1f - sphere.LinearDamping * dt);
        sphere.AngularVelocity *= MathF.Max(0f, 1f - sphere.AngularDamping * dt);

        Vector3 remaining = velocity * dt;
        if (!statics.IsValid)
        {
            sphere.Position += remaining;
            sphere.Velocity = velocity;
            return;
        }

        Collider ball = Collider.CreateSphere(sphere.Radius, settings.StaticFilter);
        Vector3 position = sphere.Position;
        for (int iteration = 0; iteration < MaxSlideIterations && remaining.LengthSquared() > MinMoveSq; iteration++)
        {
            float length = remaining.Length();
            Vector3 direction = remaining / length;
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

            float contact = (length + settings.Skin) * hit.Fraction;
            float travel = MathF.Min(contact - settings.Skin, length);
            if (travel > 0f)
            {
                position += direction * travel;
            }

            Vector3 normal = hit.SurfaceNormal;
            if (normal.LengthSquared() < Eps) break;
            normal = Vector3.Normalize(normal);

            // Resolve this contact's velocity (normal restitution + Coulomb friction with torque).
            ResolveStaticContact(ref velocity, ref sphere.AngularVelocity, sphere.Radius, sphere.InverseMass,
                sphere.InverseInertia, normal, sphere.Restitution, CombineFriction(sphere.Friction, settings.StaticFriction),
                settings);

            // Slide the leftover displacement along the surface for the rest of this tick.
            Vector3 rest = direction * (length - MathF.Max(travel, 0f));
            rest -= Vector3.Dot(rest, normal) * normal;
            remaining = rest;
        }

        sphere.Position = position;
        sphere.Velocity = velocity;
    }

    /// <summary>Pairwise sphere-sphere: depenetrate half/half along the center axis, exchange the
    /// central normal impulse (no torque), then a tangential friction impulse at the contact =
    /// "throw" (transfers spin to both).</summary>
    private static void ResolvePairs(Span<DynamicSphere> spheres, in PlanarDynamicsSettings settings)
    {
        for (int i = 0; i < spheres.Length; i++)
        {
            for (int j = i + 1; j < spheres.Length; j++)
            {
                ref DynamicSphere a = ref spheres[i];
                ref DynamicSphere b = ref spheres[j];

                Vector3 delta = b.Position - a.Position;
                float distance = delta.Length();
                float minDistance = a.Radius + b.Radius;
                if (distance >= minDistance || distance < Eps) continue;

                Vector3 n = delta / distance; // from A toward B
                float penetration = minDistance - distance;
                float invA = a.InverseMass, invB = b.InverseMass;
                float invSum = invA + invB;
                a.Position -= n * (penetration * (invA / invSum));
                b.Position += n * (penetration * (invB / invSum));

                Vector3 rA = n * a.Radius;   // A center → contact
                Vector3 rB = -n * b.Radius;  // B center → contact
                Vector3 vContactA = a.Velocity + Vector3.Cross(a.AngularVelocity, rA);
                Vector3 vContactB = b.Velocity + Vector3.Cross(b.AngularVelocity, rB);
                Vector3 vRel = vContactB - vContactA;
                float vn = Vector3.Dot(vRel, n);
                if (vn >= 0f) continue; // separating

                float e = -vn > RestitutionSlop ? 0.5f * (a.Restitution + b.Restitution) : 0f;
                float jn = -(1f + e) * vn / invSum; // central: no angular term
                a.Velocity -= n * (jn * invA);
                b.Velocity += n * (jn * invB);
                a.ContactImpulse += jn;
                b.ContactImpulse += jn;

                // Friction (throw): tangential relative velocity → impulse clamped to μ·jn.
                Vector3 vt = vRel - vn * n;
                float vtLen = vt.Length();
                if (vtLen > Eps)
                {
                    Vector3 t = vt / vtLen;
                    float invIa = a.InverseInertia, invIb = b.InverseInertia;
                    float kt = invSum
                        + Vector3.Dot(Vector3.Cross(Vector3.Cross(rA, t) * invIa, rA), t)
                        + Vector3.Dot(Vector3.Cross(Vector3.Cross(rB, t) * invIb, rB), t);
                    float jt = -vtLen / kt;
                    float maxF = CombineFriction(a.Friction, b.Friction) * jn;
                    jt = Math.Clamp(jt, -maxF, maxF);
                    Vector3 jtVec = t * jt;
                    a.Velocity -= jtVec * invA;
                    b.Velocity += jtVec * invB;
                    a.AngularVelocity -= Vector3.Cross(rA, jtVec) * invIa;
                    b.AngularVelocity += Vector3.Cross(rB, jtVec) * invIb;
                }
            }
        }
    }

    /// <summary>Resting/penetration pass: push a sphere out of the nearest static to skin clearance
    /// and resolve the contact velocity (support against gravity + friction). Reports whether the
    /// contact supports the sphere (upward normal) for sleeping.</summary>
    private static void DepenetrateAndSupport(ref DynamicSphere sphere, CollisionWorldHandle statics,
        in PlanarDynamicsSettings settings, out bool supported)
    {
        supported = false;
        if (!statics.IsValid) return;

        var input = new ColliderDistanceInput
        {
            Collider = Collider.CreateSphere(sphere.Radius, settings.StaticFilter),
            Transform = new RigidTransform(sphere.Position, Quaternion.Identity),
            MaxDistance = settings.Skin,
        };
        if (!statics.CalculateDistance(input, out DistanceHit hit)) return;
        if (hit.Distance >= settings.Skin) return;

        Vector3 normal = hit.SurfaceNormal;
        if (normal.LengthSquared() < Eps) return;
        normal = Vector3.Normalize(normal);

        sphere.Position += normal * (settings.Skin - hit.Distance);
        supported = normal.Y > SupportNormalY;

        // Reflect only an into-surface velocity (a cast bounce this step already turned it away).
        if (Vector3.Dot(sphere.Velocity + Vector3.Cross(sphere.AngularVelocity, -normal * sphere.Radius), normal) < 0f)
        {
            ResolveStaticContact(ref sphere.Velocity, ref sphere.AngularVelocity, sphere.Radius, sphere.InverseMass,
                sphere.InverseInertia, normal, sphere.Restitution,
                CombineFriction(sphere.Friction, settings.StaticFriction), settings);
        }
    }

    /// <summary>Apply a static contact to (velocity, angular velocity): central normal impulse with
    /// restitution (suppressed at rest), then a Coulomb friction impulse at the contact point that
    /// torques the sphere. <paramref name="normal"/> points from the surface toward the sphere.</summary>
    private static void ResolveStaticContact(ref Vector3 velocity, ref Vector3 angular, float radius,
        float invMass, float invInertia, Vector3 normal, float restitution, float friction,
        in PlanarDynamicsSettings settings)
    {
        Vector3 r = -normal * radius; // center → contact point
        Vector3 vContact = velocity + Vector3.Cross(angular, r);
        float vn = Vector3.Dot(vContact, normal);
        if (vn >= 0f) return; // separating

        float e = -vn > RestitutionSlop ? restitution : 0f;
        float jn = -(1f + e) * vn / invMass; // central impulse (r ∥ normal ⇒ no torque)
        velocity += normal * (jn * invMass);

        // Coulomb friction along the tangential contact velocity.
        Vector3 vt = vContact - vn * normal;
        float vtLen = vt.Length();
        if (vtLen <= Eps) return;
        Vector3 t = vt / vtLen;
        Vector3 rt = Vector3.Cross(r, t);
        float kt = invMass + Vector3.Dot(Vector3.Cross(rt * invInertia, r), t);
        float jt = -vtLen / kt;
        float maxF = friction * jn;
        jt = Math.Clamp(jt, -maxF, maxF);
        Vector3 jtVec = t * jt;
        velocity += jtVec * invMass;
        angular += Vector3.Cross(r, jtVec) * invInertia;
    }

    /// <summary>Settle a slow, supported sphere to exact rest so it doesn't creep on gravity churn.</summary>
    private static void Settle(ref DynamicSphere sphere, in PlanarDynamicsSettings settings, bool supported)
    {
        if (!supported) return;
        if (sphere.Velocity.LengthSquared() < settings.MinSpeed * settings.MinSpeed)
        {
            sphere.Velocity = Vector3.Zero;
        }
        if (sphere.AngularVelocity.LengthSquared() < settings.MinAngularSpeed * settings.MinAngularSpeed)
        {
            sphere.AngularVelocity = Vector3.Zero;
        }
    }

    /// <summary>Combine two friction coefficients (geometric mean, the common convention).</summary>
    private static float CombineFriction(float a, float b)
    {
        float product = a * b;
        return product > 0f ? MathF.Sqrt(product) : 0f;
    }

    /// <summary>Flatten a direction to the XZ plane and normalize; zero if it was vertical.</summary>
    private static Vector3 Horizontal(Vector3 v)
    {
        var flat = new Vector3(v.X, 0f, v.Z);
        float length = flat.Length();
        return length > 1e-3f ? flat / length : Vector3.Zero;
    }
}
