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

    /// <summary>Unmanaged handle for use inside ECS components/systems. Borrowed: valid while
    /// this CollisionWorld is alive; <c>default(CollisionWorldHandle)</c> = no world.</summary>
    public unsafe CollisionWorldHandle Handle => new((nint)_blob.UnsafePtr);

    /// <summary>Closest hit of the segment Start → End against all filtered bodies.</summary>
    public bool CastRay(in RaycastInput input, out RaycastHit closestHit)
        => Handle.CastRay(input, out closestHit);

    /// <summary>Closest hit of the swept collider against all filtered bodies.
    /// Filtering matches the cast collider's own <see cref="Collider.Filter"/> against each body.</summary>
    public bool CastCollider(in ColliderCastInput input, out ColliderCastHit closestHit)
        => Handle.CastCollider(input, out closestHit);

    /// <summary>Smallest surface separation between the query collider and all filtered bodies
    /// within <see cref="ColliderDistanceInput.MaxDistance"/> (negative = penetration).</summary>
    public bool CalculateDistance(in ColliderDistanceInput input, out DistanceHit closestHit)
        => Handle.CalculateDistance(input, out closestHit);

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
