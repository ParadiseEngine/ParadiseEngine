using System.Numerics;

namespace Paradise.Physics;

/// <summary>Moves a Y-aligned capsule by horizontal casting and sliding.</summary>
/// <remarks>Stops at skin clearance and projects remaining motion onto the horizontal wall plane.
/// <see cref="Move(CollisionWorldHandle, in CollisionFilter, float, float, Vector3, Vector3, float)"/> preserves
/// the input Y; <see cref="MoveGrounded(CollisionWorldHandle, in CollisionFilter, float, float, Vector3, Vector3, float, in GroundSettings, LedgePolicy, out Vector3, out GroundHit)"/>
/// also keeps ground support and places the feet on the ground.</remarks>
public static class PlanarCapsuleSlide
{
    private const int MaxSlideIterations = 4;
    private const float MinMoveSq = 1e-10f;
    private const float MinHorizontalNormal = 1e-3f;

    public static Vector3 Move(CollisionWorld statics, in CollisionFilter filter,
        float radius, float halfLength, Vector3 position, Vector3 displacement, float skin)
        => Move(statics.Handle, filter, radius, halfLength, position, displacement, skin);

    /// <summary>Handle-based overload for use inside ECS systems. An invalid handle means no
    /// statics: the move applies unobstructed.</summary>
    public static Vector3 Move(CollisionWorldHandle statics, in CollisionFilter filter,
        float radius, float halfLength, Vector3 position, Vector3 displacement, float skin)
    {
        var remaining = new Vector3(displacement.X, 0f, displacement.Z);
        float y = position.Y;
        Vector3 current = position;
        Collider capsule = Collider.CreateCapsule(radius, halfLength, filter);

        for (int iteration = 0; iteration < MaxSlideIterations && remaining.LengthSquared() > MinMoveSq; iteration++)
        {
            var input = new ColliderCastInput
            {
                Collider = capsule,
                Orientation = Quaternion.Identity,
                Start = current,
                End = current + remaining,
            };
            if (!statics.CastCollider(input, out ColliderCastHit hit))
            {
                current += remaining;
                break;
            }

            // Advance to a skin's clearance short of the contact. A fraction-0 hit (already
            // touching) advances nothing and the remainder slides along the wall instead.
            float length = remaining.Length();
            Vector3 direction = remaining / length;
            float travel = length * hit.Fraction - skin;
            if (travel > 0f)
            {
                current += direction * travel;
            }

            var normal = new Vector3(hit.SurfaceNormal.X, 0f, hit.SurfaceNormal.Z);
            float normalLength = normal.Length();
            if (normalLength < MinHorizontalNormal)
            {
                break; // near-vertical normal (floor/ceiling-ish): planar backstop, stop here
            }

            normal /= normalLength;
            Vector3 rest = remaining * (1f - hit.Fraction);
            remaining = rest - Vector3.Dot(rest, normal) * normal;
            float into = Vector3.Dot(remaining, normal);
            if (into < 0f)
            {
                remaining -= into * normal; // numeric guard: never slide INTO the wall
            }
        }

        return new Vector3(current.X, y, current.Z);
    }

    /// <summary>Slides the capsule like <see cref="Move(CollisionWorldHandle, in CollisionFilter, float, float, Vector3, Vector3, float)"/>,
    /// applies <paramref name="ledgePolicy"/> and snaps its feet to the ground; true when it ends grounded.</summary>
    /// <param name="position">Capsule centre; the feet are <c>halfLength + radius</c> below it.</param>
    /// <param name="result">Capsule centre after the move. Without ground in range it keeps the input Y.</param>
    /// <param name="groundHit">Ground under <paramref name="result"/>; its <see cref="GroundHit.Distance"/> is 0 once the feet are placed on it.</param>
    /// <remarks>A grounded capsule slides raised by <see cref="GroundSettings.StepHeight"/>, so the ground it
    /// stands on and obstacles up to that height do not block it. Its feet then follow the ground along the move
    /// as <see cref="CharacterGround.FindLedge(CollisionWorldHandle, in GroundSettings, in GroundHit, Vector3, out GroundLedge)"/>
    /// does, so long moves down or up walkable slopes stay grounded. The ledge policy applies only when the move
    /// starts grounded; an airborne capsule moves freely and lands on ground within the probe band.</remarks>
    public static bool MoveGrounded(CollisionWorld statics, in CollisionFilter filter,
        float radius, float halfLength, Vector3 position, Vector3 displacement, float skin,
        in GroundSettings ground, LedgePolicy ledgePolicy, out Vector3 result, out GroundHit groundHit)
        => MoveGrounded(statics.Handle, filter, radius, halfLength, position, displacement, skin,
            ground, ledgePolicy, out result, out groundHit);

    /// <summary>Handle-based overload for use inside ECS systems.</summary>
    public static bool MoveGrounded(CollisionWorldHandle statics, in CollisionFilter filter,
        float radius, float halfLength, Vector3 position, Vector3 displacement, float skin,
        in GroundSettings ground, LedgePolicy ledgePolicy, out Vector3 result, out GroundHit groundHit)
    {
        float feetOffset = halfLength + radius;
        var feet = new Vector3(position.X, position.Y - feetOffset, position.Z);
        if (!CharacterGround.Probe(statics, ground, feet, out GroundHit start))
        {
            Vector3 airborne = Move(statics, filter, radius, halfLength, position, displacement, skin);
            return Land(statics, ground, airborne, airborne.Y - feetOffset, feetOffset, out result, out groundHit);
        }

        var planar = new Vector3(displacement.X, 0f, displacement.Z);
        bool endTracked = ledgePolicy != LedgePolicy.StepOff;
        GroundHit end = default;
        if (endTracked)
        {
            planar = CharacterGround.KeepSupport(statics, ground, start, planar, ledgePolicy == LedgePolicy.Slide, out end);
        }

        var lift = new Vector3(0f, ground.StepHeight, 0f);
        Vector3 slid = Move(statics, filter, radius, halfLength, position + lift, planar, skin);
        var moved = new Vector3(slid.X - position.X, 0f, slid.Z - position.Z);
        if (!endTracked || Vector3.DistanceSquared(moved, planar) > MinMoveSq)
        {
            // A wall slide changes the path, possibly off an edge the constrained move kept clear of.
            bool lost = CharacterGround.FindLedge(statics, ground, start, moved, out GroundLedge ledge);
            end = ledge.Edge;
            if (lost && ledgePolicy == LedgePolicy.StepOff)
            {
                Vector3 off = position + moved;
                return Land(statics, ground, off, end.Position.Y + start.Distance, feetOffset, out result, out groundHit);
            }

            moved *= ledge.SupportedFraction;
        }

        result = (position + moved) with { Y = end.Position.Y + feetOffset };
        groundHit = end with { Distance = 0f };
        return true;
    }

    private static bool Land(CollisionWorldHandle statics, in GroundSettings ground, Vector3 position, float feetY,
        float feetOffset, out Vector3 result, out GroundHit groundHit)
    {
        if (!CharacterGround.Probe(statics, ground, position with { Y = feetY }, out groundHit))
        {
            result = position;
            return false;
        }

        result = position with { Y = groundHit.Position.Y + feetOffset };
        groundHit.Distance = 0f;
        return true;
    }
}
