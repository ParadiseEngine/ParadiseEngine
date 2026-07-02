using System.Numerics;
using Paradise.BLOB;

namespace Paradise.Physics;

/// <summary>
/// Immutable set of static colliders with closest-hit queries. Stateless by design
/// (Unity Physics philosophy): <see cref="Build"/> is a pure function of its inputs, there are
/// no caches and no incremental updates — rebuild when the set changes. Queries allocate
/// nothing, are safe to run concurrently from any thread, and break fraction/distance ties by
/// the lowest body index so results are order-deterministic.
///
/// Storage is a single Paradise.BLOB blob in unmanaged memory (NativeMemory — no GC-heap
/// pinning): parallel <see cref="BlobArray{T}"/>s of colliders/transforms/AABBs plus a preorder
/// <see cref="BlobTree{T}"/> BVH (median split on the longest axis of the node's AABB-union
/// extent, deterministic construction). Disposing frees the allocation; the blob finalizer is
/// the backstop if the owner never does.
/// </summary>
public sealed class CollisionWorld : IDisposable
{
    /// <summary>BVH node: internal nodes bound their subtree, leaves reference one body.</summary>
    internal struct BvhNode
    {
        public Aabb Bounds;
        public int BodyIndex; // -1 for internal nodes
    }

    internal struct WorldData
    {
        public BlobArray<Collider> Colliders;
        public BlobArray<RigidTransform> Transforms;
        public BlobArray<Aabb> Aabbs;
        public BlobTree<BvhNode> Bvh;
    }

    private readonly NativeBlobAssetReference<WorldData> _blob;

    private CollisionWorld(NativeBlobAssetReference<WorldData> blob) => _blob = blob;

    public int NumBodies => _blob.Value.Colliders.Length;

    public Collider GetCollider(int bodyIndex) => _blob.Value.Colliders[bodyIndex];

    public RigidTransform GetTransform(int bodyIndex) => _blob.Value.Transforms[bodyIndex];

    public void Dispose() => _blob.Dispose();

    /// <summary>Builds a world from parallel spans of colliders and poses. Inputs are copied
    /// into one contiguous blob; a BVH is built over the body AABBs.</summary>
    public static CollisionWorld Build(ReadOnlySpan<Collider> colliders, ReadOnlySpan<RigidTransform> transforms)
    {
        if (colliders.Length != transforms.Length)
            throw new ArgumentException($"Collider count ({colliders.Length}) must match transform count ({transforms.Length}).");

        var colliderArray = colliders.ToArray();
        var transformArray = transforms.ToArray();
        var aabbs = new Aabb[colliderArray.Length];
        for (int i = 0; i < colliderArray.Length; i++)
            aabbs[i] = colliderArray[i].CalculateAabb(transformArray[i]);

        var builder = new StructBuilder<WorldData>();
        builder.SetArray(ref builder.Value.Colliders, colliderArray);
        builder.SetArray(ref builder.Value.Transforms, transformArray);
        builder.SetArray(ref builder.Value.Aabbs, aabbs);
        if (aabbs.Length > 0)
        {
            int[] indices = new int[aabbs.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            builder.SetTree(ref builder.Value.Bvh, BuildBvhNode(aabbs, indices.AsMemory()));
        }
        else
        {
            builder.SetBuilder(ref builder.Value.Bvh, new TreeBuilder<BvhNode>());
        }

        return new CollisionWorld(builder.CreateNativeBlobAssetReference());
    }

    /// <summary>Closest hit of the segment Start → End against all filtered bodies.</summary>
    public bool CastRay(in RaycastInput input, out RaycastHit closestHit)
    {
        closestHit = default;
        ref WorldData data = ref _blob.Value;
        Vector3 displacement = input.End - input.Start;
        float best = float.PositiveInfinity;
        int bestBody = int.MaxValue;
        bool found = false;

        int node = 0;
        int length = data.Bvh.Length;
        while (node < length)
        {
            ref BvhNode bvh = ref data.Bvh.GetValue(node);
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
        ref WorldData data = ref _blob.Value;
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
            ref BvhNode bvh = ref data.Bvh.GetValue(node);
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
        ref WorldData data = ref _blob.Value;
        Aabb queryAabb = input.Collider.CalculateAabb(input.Transform).Expanded(new Vector3(input.MaxDistance));

        float best = float.PositiveInfinity;
        int bestBody = int.MaxValue;
        bool found = false;

        int node = 0;
        int length = data.Bvh.Length;
        while (node < length)
        {
            ref BvhNode bvh = ref data.Bvh.GetValue(node);
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

    // ---- BVH construction (deterministic median split) -----------------------

    private sealed class BuildNode : ITreeNode<BvhNode>
    {
        public required IBuilder<BvhNode> ValueBuilder { get; init; }
        public required IReadOnlyList<ITreeNode<BvhNode>> Children { get; init; }
    }

    private static BuildNode BuildBvhNode(Aabb[] aabbs, Memory<int> indices)
    {
        Span<int> span = indices.Span;
        Aabb bounds = aabbs[span[0]];
        for (int i = 1; i < span.Length; i++) bounds.Include(aabbs[span[i]]);

        if (span.Length == 1)
        {
            return new BuildNode
            {
                ValueBuilder = new ValueBuilder<BvhNode>(new BvhNode { Bounds = bounds, BodyIndex = span[0] }),
                Children = [],
            };
        }

        // Median split on the longest axis of this node's AABB-union extent; the split point
        // itself sorts bodies by centroid on that axis, with ties falling back to the body
        // index so construction is fully deterministic.
        Vector3 extent = bounds.Max - bounds.Min;
        int axis = extent.X >= extent.Y
            ? (extent.X >= extent.Z ? 0 : 2)
            : (extent.Y >= extent.Z ? 1 : 2);
        int[] sorted = indices.ToArray();
        Array.Sort(sorted, (a, b) =>
        {
            float ca = Centroid(aabbs[a], axis);
            float cb = Centroid(aabbs[b], axis);
            int compare = ca.CompareTo(cb);
            return compare != 0 ? compare : a.CompareTo(b);
        });
        sorted.CopyTo(indices);

        int mid = span.Length / 2;
        return new BuildNode
        {
            ValueBuilder = new ValueBuilder<BvhNode>(new BvhNode { Bounds = bounds, BodyIndex = -1 }),
            Children = [BuildBvhNode(aabbs, indices[..mid]), BuildBvhNode(aabbs, indices[mid..])],
        };

        static float Centroid(in Aabb aabb, int axis) => axis switch
        {
            0 => aabb.Min.X + aabb.Max.X,
            1 => aabb.Min.Y + aabb.Max.Y,
            _ => aabb.Min.Z + aabb.Max.Z,
        };
    }
}
