using System.Numerics;

namespace Paradise.Physics;

/// <summary>
/// Horizontal cast-and-slide movement for a Y-aligned capsule against a static
/// <see cref="CollisionWorld"/>: advance to a skin's clearance short of the first contact, then
/// project the remainder onto the (horizontally flattened) wall plane and repeat.
/// Planar contract: the returned position always keeps the input Y.
/// </summary>
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
}
