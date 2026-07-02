using System.Numerics;

namespace Paradise.Physics;

/// <summary>
/// Immutable set of static colliders with closest-hit queries. Stateless by design
/// (Unity Physics philosophy): <see cref="Build"/> is a pure function of its inputs, there are
/// no caches and no incremental updates — rebuild when the set changes. Queries allocate
/// nothing, are safe to run concurrently from any thread, and break fraction ties by the
/// lowest body index so results are order-deterministic.
/// </summary>
public sealed class CollisionWorld
{
    private readonly Collider[] _colliders;
    private readonly RigidTransform[] _transforms;
    private readonly Aabb[] _aabbs;

    private CollisionWorld(Collider[] colliders, RigidTransform[] transforms, Aabb[] aabbs)
    {
        _colliders = colliders;
        _transforms = transforms;
        _aabbs = aabbs;
    }

    public int NumBodies => _colliders.Length;

    public Collider GetCollider(int bodyIndex) => _colliders[bodyIndex];

    public RigidTransform GetTransform(int bodyIndex) => _transforms[bodyIndex];

    /// <summary>Builds a world from parallel spans of colliders and poses. Inputs are copied.</summary>
    public static CollisionWorld Build(ReadOnlySpan<Collider> colliders, ReadOnlySpan<RigidTransform> transforms)
    {
        if (colliders.Length != transforms.Length)
            throw new ArgumentException($"Collider count ({colliders.Length}) must match transform count ({transforms.Length}).");

        var colliderArray = colliders.ToArray();
        var transformArray = transforms.ToArray();
        var aabbs = new Aabb[colliderArray.Length];
        for (int i = 0; i < colliderArray.Length; i++)
            aabbs[i] = colliderArray[i].CalculateAabb(transformArray[i]);
        return new CollisionWorld(colliderArray, transformArray, aabbs);
    }

    /// <summary>Closest hit of the segment Start → End against all filtered bodies.</summary>
    public bool CastRay(in RaycastInput input, out RaycastHit closestHit)
    {
        closestHit = default;
        Vector3 displacement = input.End - input.Start;
        float best = float.PositiveInfinity;
        bool found = false;

        for (int i = 0; i < _colliders.Length; i++)
        {
            if (!CollisionFilter.IsCollisionEnabled(input.Filter, _colliders[i].Filter)) continue;
            if (!_aabbs[i].IntersectsSegment(input.Start, displacement, out float entry)) continue;
            if (entry >= best) continue; // AABB entry lower-bounds the true fraction
            if (!RaycastQueries.Raycast(_colliders[i], _transforms[i], input.Start, input.End, out float fraction, out Vector3 normal)) continue;
            if (fraction >= best) continue; // strict '<': ties keep the lowest body index

            best = fraction;
            found = true;
            closestHit = new RaycastHit
            {
                Fraction = fraction,
                Position = input.Start + displacement * fraction,
                SurfaceNormal = normal,
                BodyIndex = i,
            };
        }

        return found;
    }

    /// <summary>Closest hit of the swept collider against all filtered bodies.
    /// Filtering matches the cast collider's own <see cref="Collider.Filter"/> against each body.</summary>
    public bool CastCollider(in ColliderCastInput input, out ColliderCastHit closestHit)
    {
        closestHit = default;
        var startPose = new RigidTransform(input.Start, input.Orientation);
        var endPose = new RigidTransform(input.End, input.Orientation);
        Aabb sweptAabb = input.Collider.CalculateAabb(startPose);
        sweptAabb.Include(input.Collider.CalculateAabb(endPose));

        float best = float.PositiveInfinity;
        bool found = false;

        for (int i = 0; i < _colliders.Length; i++)
        {
            if (!CollisionFilter.IsCollisionEnabled(input.Collider.Filter, _colliders[i].Filter)) continue;
            if (!sweptAabb.Overlaps(_aabbs[i])) continue;
            if (!ColliderCastQueries.Cast(input, _colliders[i], _transforms[i], out ColliderCastHit hit)) continue;
            if (hit.Fraction >= best) continue;

            best = hit.Fraction;
            found = true;
            closestHit = hit;
            closestHit.BodyIndex = i;
        }

        return found;
    }

    /// <summary>Smallest surface separation between the query collider and all filtered bodies
    /// within <see cref="ColliderDistanceInput.MaxDistance"/> (negative = penetration).</summary>
    public bool CalculateDistance(in ColliderDistanceInput input, out DistanceHit closestHit)
    {
        closestHit = default;
        Aabb queryAabb = input.Collider.CalculateAabb(input.Transform).Expanded(new Vector3(input.MaxDistance));

        float best = float.PositiveInfinity;
        bool found = false;

        for (int i = 0; i < _colliders.Length; i++)
        {
            if (!CollisionFilter.IsCollisionEnabled(input.Collider.Filter, _colliders[i].Filter)) continue;
            if (!queryAabb.Overlaps(_aabbs[i])) continue;

            DistanceResult distance = ConvexConvexDistance.Distance(input.Collider, input.Transform, _colliders[i], _transforms[i]);
            if (distance.Distance > input.MaxDistance || distance.Distance >= best) continue;

            best = distance.Distance;
            found = true;
            closestHit = new DistanceHit
            {
                Distance = distance.Distance,
                Position = distance.ClosestB,
                SurfaceNormal = distance.NormalBToA,
                BodyIndex = i,
            };
        }

        return found;
    }
}
