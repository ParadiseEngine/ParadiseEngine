using System.Numerics;

namespace Paradise.Physics;

/// <summary>
/// Queries against a single collider at a known pose. Unlike <see cref="CollisionWorld"/>
/// queries these do NOT apply collision filtering — the caller chose the target explicitly.
/// </summary>
public static class ColliderQueries
{
    public static bool RayCollider(in RaycastInput input, in Collider collider, in RigidTransform transform, out RaycastHit hit)
    {
        if (RaycastQueries.Raycast(collider, transform, input.Start, input.End, out float fraction, out Vector3 normal))
        {
            hit = new RaycastHit
            {
                Fraction = fraction,
                Position = input.Start + (input.End - input.Start) * fraction,
                SurfaceNormal = normal,
                BodyIndex = -1,
            };
            return true;
        }

        hit = default;
        return false;
    }

    public static bool CastCollider(in ColliderCastInput input, in Collider target, in RigidTransform targetTransform, out ColliderCastHit hit)
        => ColliderCastQueries.Cast(input, target, targetTransform, out hit);

    public static bool DistanceBetween(in Collider a, in RigidTransform transformA, in Collider b, in RigidTransform transformB, out DistanceHit result)
    {
        DistanceResult distance = ConvexConvexDistance.Distance(a, transformA, b, transformB);
        result = new DistanceHit
        {
            Distance = distance.Distance,
            Position = distance.ClosestB,
            SurfaceNormal = distance.NormalBToA,
            BodyIndex = -1,
        };
        return true;
    }
}
