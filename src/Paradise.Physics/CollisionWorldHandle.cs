using System.Numerics;

namespace Paradise.Physics;

/// <summary>
/// Unmanaged handle to a <see cref="CollisionWorld"/>'s native blob — usable inside ECS
/// components and generated systems where the managed class cannot go. Carries the full query
/// API; <c>default</c> is the invalid handle (all queries miss), which models "no collision
/// world" without nullable references.
///
/// LIFETIME: borrowed — valid while the owning <see cref="CollisionWorld"/> is alive
/// (typically the whole session; the simulation runner owns it). Never store a handle beyond
/// the owner's lifetime.
/// </summary>
public readonly unsafe struct CollisionWorldHandle
{
    private readonly nint _worldData;

    internal CollisionWorldHandle(nint worldData) => _worldData = worldData;

    /// <summary>False for <c>default</c> — queries on an invalid handle simply miss.</summary>
    public bool IsValid => _worldData != 0;

    private ref CollisionWorld.WorldData Data => ref *(CollisionWorld.WorldData*)_worldData;

    /// <summary>Closest hit of the segment Start → End against all filtered bodies.</summary>
    public bool CastRay(in RaycastInput input, out RaycastHit closestHit)
    {
        closestHit = default;
        if (!IsValid) return false;
        ref CollisionWorld.WorldData data = ref Data;
        Vector3 displacement = input.End - input.Start;
        float best = float.PositiveInfinity;
        int bestBody = int.MaxValue;
        bool found = false;

        int node = 0;
        int length = data.Bvh.Length;
        while (node < length)
        {
            ref CollisionWorld.BvhNode bvh = ref data.Bvh.GetValue(node);
            // Entry fraction lower-bounds every fraction in the subtree; strictly-worse
            // subtrees are skipped (ties are kept so the lowest-index rule holds).
            if (!bvh.Bounds.IntersectsSegment(input.Start, displacement, out float entry) || entry > best)
            {
                node = data.Bvh.GetEndIndex(node);
                continue;
            }

            int body = bvh.BodyIndex;
            node++;
            if (body < 0) continue;
            if (!CollisionFilter.IsCollisionEnabled(input.Filter, data.Colliders[body].Filter)) continue;
            if (!RaycastQueries.Raycast(data.Colliders[body], data.Transforms[body], input.Start, input.End,
                    out float fraction, out Vector3 normal)) continue;
            if (fraction > best || (fraction == best && body > bestBody)) continue;

            best = fraction;
            bestBody = body;
            found = true;
            closestHit = new RaycastHit
            {
                Fraction = fraction,
                Position = input.Start + displacement * fraction,
                SurfaceNormal = normal,
                BodyIndex = body,
            };
        }

        return found;
    }

    /// <summary>Closest hit of the swept collider against all filtered bodies.
    /// Filtering matches the cast collider's own <see cref="Collider.Filter"/> against each body.</summary>
    public bool CastCollider(in ColliderCastInput input, out ColliderCastHit closestHit)
    {
        closestHit = default;
        if (!IsValid) return false;
        ref CollisionWorld.WorldData data = ref Data;
        var startPose = new RigidTransform(input.Start, input.Orientation);
        var endPose = new RigidTransform(input.End, input.Orientation);
        Aabb sweptAabb = input.Collider.CalculateAabb(startPose);
        sweptAabb.Include(input.Collider.CalculateAabb(endPose));

        float best = float.PositiveInfinity;
        int bestBody = int.MaxValue;
        bool found = false;

        int node = 0;
        int length = data.Bvh.Length;
        while (node < length)
        {
            ref CollisionWorld.BvhNode bvh = ref data.Bvh.GetValue(node);
            if (!sweptAabb.Overlaps(bvh.Bounds))
            {
                node = data.Bvh.GetEndIndex(node);
                continue;
            }

            int body = bvh.BodyIndex;
            node++;
            if (body < 0) continue;
            if (!CollisionFilter.IsCollisionEnabled(input.Collider.Filter, data.Colliders[body].Filter)) continue;
            if (!ColliderCastQueries.Cast(input, data.Colliders[body], data.Transforms[body], out ColliderCastHit hit)) continue;
            if (hit.Fraction > best || (hit.Fraction == best && body > bestBody)) continue;

            best = hit.Fraction;
            bestBody = body;
            found = true;
            closestHit = hit;
            closestHit.BodyIndex = body;
        }

        return found;
    }

    /// <summary>Smallest surface separation between the query collider and all filtered bodies
    /// within <see cref="ColliderDistanceInput.MaxDistance"/> (negative = penetration).</summary>
    public bool CalculateDistance(in ColliderDistanceInput input, out DistanceHit closestHit)
    {
        closestHit = default;
        if (!IsValid) return false;
        ref CollisionWorld.WorldData data = ref Data;
        Aabb queryAabb = input.Collider.CalculateAabb(input.Transform).Expanded(new Vector3(input.MaxDistance));

        float best = float.PositiveInfinity;
        int bestBody = int.MaxValue;
        bool found = false;

        int node = 0;
        int length = data.Bvh.Length;
        while (node < length)
        {
            ref CollisionWorld.BvhNode bvh = ref data.Bvh.GetValue(node);
            if (!queryAabb.Overlaps(bvh.Bounds))
            {
                node = data.Bvh.GetEndIndex(node);
                continue;
            }

            int body = bvh.BodyIndex;
            node++;
            if (body < 0) continue;
            if (!CollisionFilter.IsCollisionEnabled(input.Collider.Filter, data.Colliders[body].Filter)) continue;

            DistanceResult distance = ConvexConvexDistance.Distance(input.Collider, input.Transform,
                data.Colliders[body], data.Transforms[body]);
            if (distance.Distance > input.MaxDistance) continue;
            if (distance.Distance > best || (distance.Distance == best && body > bestBody)) continue;

            best = distance.Distance;
            bestBody = body;
            found = true;
            closestHit = new DistanceHit
            {
                Distance = distance.Distance,
                Position = distance.ClosestB,
                SurfaceNormal = distance.NormalBToA,
                BodyIndex = body,
            };
        }

        return found;
    }
}
