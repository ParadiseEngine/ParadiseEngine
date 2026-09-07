using System.Numerics;

namespace Paradise.Geometry;

/// <summary>What a traversal asks of a leaf slot: test the item there against the ray and shrink
/// the ray's maximum distance on a closer hit. A struct constraint so the walk inlines it.</summary>
public interface IBvhLeafIntersector
{
    /// <param name="slot">Index into <see cref="WideBvh.ItemOrder"/>.</param>
    bool Intersect(int slot, Vector3 origin, Vector3 direction, ref float tMax);
}

/// <summary>The CPU reference walk over a <see cref="WideBvh"/>: the same node decode, the same
/// slab test and the same closest-hit discipline as the shader. It exists to prove the builder
/// and to give tests an oracle; the renderer never runs it per frame.</summary>
public static class BvhTraversal
{
    /// <summary>Entries the walk's stack holds — the same as the shader's. A hierarchy needs
    /// <see cref="WideBvh.RequiredStackDepth"/> of them, which the builder reports.</summary>
    public const int StackDepth = 64;

    /// <summary>Closest hit within <paramref name="tMax"/>, which is shrunk to the hit distance.
    /// Returns true when anything was hit.</summary>
    public static bool ClosestHit<TLeaf>(ReadOnlySpan<BvhNode> nodes, Vector3 origin, Vector3 direction, ref float tMax, ref TLeaf leaf)
        where TLeaf : struct, IBvhLeafIntersector
    {
        var invDirection = Aabb.InverseDirection(direction);
        Span<uint> stack = stackalloc uint[StackDepth];
        var depth = 0;
        stack[depth++] = 0;
        var hit = false;

        while (depth > 0)
        {
            ref readonly var node = ref nodes[(int)stack[--depth]];
            for (var child = 0; child < BvhNode.ChildCount; child++)
            {
                var meta = node.GetMeta(child);
                if (meta == BvhNode.EmptyChild) continue;
                if (float.IsPositiveInfinity(node.ChildBounds(child).Intersect(origin, invDirection, tMax))) continue;

                if (BvhNode.IsLeaf(meta))
                {
                    var first = (int)node.LeafBase + BvhNode.LeafItemOffset(meta);
                    var count = BvhNode.LeafItemCount(meta);
                    for (var i = first; i < first + count; i++)
                        hit |= leaf.Intersect(i, origin, direction, ref tMax);
                }
                else
                {
                    if (depth == StackDepth) throw new InvalidOperationException("BVH traversal stack overflow.");
                    stack[depth++] = node.ChildBase + (uint)BvhNode.InternalSlot(meta);
                }
            }
        }
        return hit;
    }
}
