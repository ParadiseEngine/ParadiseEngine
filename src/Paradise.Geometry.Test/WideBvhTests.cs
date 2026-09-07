using System.Runtime.CompilerServices;

namespace Paradise.Geometry.Test;

/// <summary>The builder's contract with the tracer: every item reachable, every decoded child box
/// containing its exact box, and a closest-hit walk agreeing with brute force on random rays.</summary>
public class WideBvhTests
{
    private static (Vector3[] Positions, uint[] Indices) RandomSoup(int triangles, int seed, float extent = 10f)
    {
        var random = new Random(seed);
        var positions = new Vector3[triangles * 3];
        var indices = new uint[triangles * 3];
        for (var i = 0; i < triangles; i++)
        {
            var center = new Vector3(
                (float)(random.NextDouble() * 2 - 1) * extent,
                (float)(random.NextDouble() * 2 - 1) * extent,
                (float)(random.NextDouble() * 2 - 1) * extent);
            for (var k = 0; k < 3; k++)
            {
                positions[i * 3 + k] = center + new Vector3(
                    (float)(random.NextDouble() - 0.5),
                    (float)(random.NextDouble() - 0.5),
                    (float)(random.NextDouble() - 0.5));
                indices[i * 3 + k] = (uint)(i * 3 + k);
            }
        }
        return (positions, indices);
    }

    private static bool BruteForce(Vector3[] positions, uint[] indices, Vector3 origin, Vector3 direction, out float t)
    {
        t = float.PositiveInfinity;
        var hit = false;
        for (var i = 0; i < indices.Length; i += 3)
        {
            if (RayTriangle.Intersect(origin, direction, positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]], t, out var candidate))
            {
                t = candidate;
                hit = true;
            }
        }
        return hit;
    }

    [Test]
    public async Task the_node_layout_is_the_shader_layout()
    {
        await Assert.That(Unsafe.SizeOf<BvhNode>()).IsEqualTo(96);
        var meta = BvhNode.LeafMeta(itemOffset: 17, itemCount: 5);
        await Assert.That(BvhNode.IsLeaf(meta)).IsTrue();
        await Assert.That(BvhNode.LeafItemOffset(meta)).IsEqualTo(17);
        await Assert.That(BvhNode.LeafItemCount(meta)).IsEqualTo(5);
        var internalMeta = BvhNode.InternalMeta(6);
        await Assert.That(BvhNode.IsInternal(internalMeta)).IsTrue();
        await Assert.That(BvhNode.IsLeaf(internalMeta)).IsFalse();
        await Assert.That(BvhNode.InternalSlot(internalMeta)).IsEqualTo(6);

        var node = new BvhNode();
        for (var child = 0; child < 8; child++)
        {
            node.SetMeta(child, (ushort)(0x1000 + child));
            node.SetLo(2, child, (byte)(200 + child));
            node.SetHi(1, child, (byte)(100 + child));
        }
        for (var child = 0; child < 8; child++)
        {
            await Assert.That(node.GetMeta(child)).IsEqualTo((ushort)(0x1000 + child));
            await Assert.That(node.GetLo(2, child)).IsEqualTo((byte)(200 + child));
            await Assert.That(node.GetHi(1, child)).IsEqualTo((byte)(100 + child));
            await Assert.That(node.GetLo(0, child)).IsEqualTo((byte)0);
        }
    }

    [Test]
    public async Task every_triangle_appears_exactly_once_in_the_leaf_order()
    {
        var (positions, indices) = RandomSoup(1000, seed: 1);
        var bvh = TriangleBvh.Build(positions, indices);

        await Assert.That(bvh.ItemCount).IsEqualTo(1000);
        await Assert.That(bvh.ItemOrder.Distinct().Count()).IsEqualTo(1000);
        await Assert.That(bvh.ItemOrder.Min()).IsEqualTo(0);
        await Assert.That(bvh.ItemOrder.Max()).IsEqualTo(999);

        // Walking every leaf run through the nodes covers the whole item order, exactly once.
        var covered = new int[1000];
        var internalChildren = 0;
        foreach (var node in bvh.Nodes)
        {
            for (var child = 0; child < BvhNode.ChildCount; child++)
            {
                var meta = node.GetMeta(child);
                if (meta == BvhNode.EmptyChild) continue;
                if (BvhNode.IsInternal(meta))
                {
                    internalChildren++;
                    continue;
                }
                var first = (int)node.LeafBase + BvhNode.LeafItemOffset(meta);
                for (var i = first; i < first + BvhNode.LeafItemCount(meta); i++) covered[bvh.ItemOrder[i]]++;
            }
        }
        await Assert.That(covered.All(c => c == 1)).IsTrue();
        // Every node but the root is somebody's internal child.
        await Assert.That(internalChildren).IsEqualTo(bvh.Nodes.Length - 1);
    }

    [Test]
    public async Task decoded_child_bounds_contain_the_exact_bounds()
    {
        var (positions, indices) = RandomSoup(3000, seed: 2, extent: 1000f);
        var bvh = TriangleBvh.Build(positions, indices);

        // Exact bounds of each leaf run and of each internal child's subtree, by recursion.
        Aabb Exact(int nodeIndex)
        {
            var node = bvh.Nodes[nodeIndex];
            var total = Aabb.Empty;
            for (var child = 0; child < BvhNode.ChildCount; child++)
            {
                var meta = node.GetMeta(child);
                if (meta == BvhNode.EmptyChild) continue;
                Aabb exact;
                if (BvhNode.IsInternal(meta))
                {
                    exact = Exact((int)node.ChildBase + BvhNode.InternalSlot(meta));
                }
                else
                {
                    exact = Aabb.Empty;
                    var first = (int)node.LeafBase + BvhNode.LeafItemOffset(meta);
                    for (var i = first; i < first + BvhNode.LeafItemCount(meta); i++)
                        for (var k = 0; k < 3; k++)
                            exact.Include(positions[indices[bvh.ItemOrder[i] * 3 + k]]);
                }
                var decoded = node.ChildBounds(child);
                if (decoded.Min.X > exact.Min.X || decoded.Min.Y > exact.Min.Y || decoded.Min.Z > exact.Min.Z ||
                    decoded.Max.X < exact.Max.X || decoded.Max.Y < exact.Max.Y || decoded.Max.Z < exact.Max.Z)
                    throw new InvalidOperationException($"Node {nodeIndex} child {child}: decoded {decoded.Min}..{decoded.Max} does not contain {exact.Min}..{exact.Max}.");
                // ...and is not absurdly loose: within two quantization steps per side.
                var slack = new Vector3(node.Scale(0), node.Scale(1), node.Scale(2)) * 2f;
                if ((exact.Min - decoded.Min).Length() > slack.Length() || (decoded.Max - exact.Max).Length() > slack.Length())
                    throw new InvalidOperationException($"Node {nodeIndex} child {child}: decoded bounds are looser than the quantization step.");
                total.Include(in exact);
            }
            return total;
        }

        var root = Exact(0);
        await Assert.That(root.Min).IsEqualTo(bvh.Bounds.Min);
        await Assert.That(root.Max).IsEqualTo(bvh.Bounds.Max);
    }

    [Test]
    public async Task closest_hit_agrees_with_brute_force()
    {
        var (positions, indices) = RandomSoup(2000, seed: 3);
        var bvh = TriangleBvh.Build(positions, indices);
        var leaf = new TriangleBvh.Intersector(bvh, positions, indices);
        var random = new Random(4);
        var hits = 0;

        for (var ray = 0; ray < 500; ray++)
        {
            var origin = new Vector3(
                (float)(random.NextDouble() * 2 - 1) * 12f,
                (float)(random.NextDouble() * 2 - 1) * 12f,
                (float)(random.NextDouble() * 2 - 1) * 12f);
            // Aimed into the soup rather than uniformly random, so enough rays hit for the
            // comparison to mean something.
            var target = new Vector3(
                (float)(random.NextDouble() * 2 - 1) * 5f,
                (float)(random.NextDouble() * 2 - 1) * 5f,
                (float)(random.NextDouble() * 2 - 1) * 5f);
            var direction = Vector3.Normalize(target - origin);

            var expectedHit = BruteForce(positions, indices, origin, direction, out var expectedT);
            var t = float.PositiveInfinity;
            var hit = BvhTraversal.ClosestHit(bvh.Nodes, origin, direction, ref t, ref leaf);

            if (hit != expectedHit || (hit && MathF.Abs(t - expectedT) > 1e-4f))
                throw new InvalidOperationException($"Ray {ray}: bvh {(hit ? t : float.NaN)} vs brute {(expectedHit ? expectedT : float.NaN)}.");
            if (hit) hits++;
        }
        // The soup is dense enough that the comparison is not vacuous.
        await Assert.That(hits).IsGreaterThan(100);
    }

    [Test]
    public async Task axis_aligned_rays_along_a_flat_axis_still_hit()
    {
        // A single flat quad: one axis has zero extent, which exercises the flat-axis step and the
        // infinite inverse direction component of the slab test.
        Vector3[] positions = [new(-1, 0, -1), new(1, 0, -1), new(1, 0, 1), new(-1, 0, 1)];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var bvh = TriangleBvh.Build(positions, indices);
        var leaf = new TriangleBvh.Intersector(bvh, positions, indices);

        var t = float.PositiveInfinity;
        var hit = BvhTraversal.ClosestHit(bvh.Nodes, new Vector3(0.2f, 5f, 0.3f), new Vector3(0f, -1f, 0f), ref t, ref leaf);
        await Assert.That(hit).IsTrue();
        await Assert.That(t).IsEqualTo(5f).Within(1e-5f);

        t = float.PositiveInfinity;
        var miss = BvhTraversal.ClosestHit(bvh.Nodes, new Vector3(3f, 5f, 0f), new Vector3(0f, -1f, 0f), ref t, ref leaf);
        await Assert.That(miss).IsFalse();
    }

    [Test]
    public async Task an_empty_mesh_builds_a_root_with_no_children()
    {
        var bvh = TriangleBvh.Build([], []);
        await Assert.That(bvh.Nodes.Length).IsEqualTo(1);
        await Assert.That(bvh.ItemCount).IsEqualTo(0);
        await Assert.That(bvh.Bounds.IsEmpty).IsTrue();
        for (var child = 0; child < BvhNode.ChildCount; child++)
            await Assert.That(bvh.Nodes[0].GetMeta(child)).IsEqualTo(BvhNode.EmptyChild);

        var leaf = new TriangleBvh.Intersector(bvh, [], []);
        var t = float.PositiveInfinity;
        await Assert.That(BvhTraversal.ClosestHit(bvh.Nodes, Vector3.Zero, Vector3.UnitZ, ref t, ref leaf)).IsFalse();
    }

    [Test]
    public async Task coincident_centroids_do_not_recurse_forever()
    {
        // 200 identical triangles: no split plane separates any centroid.
        var positions = new Vector3[600];
        var indices = new uint[600];
        for (var i = 0; i < 200; i++)
        {
            positions[i * 3] = new Vector3(0, 0, 0);
            positions[i * 3 + 1] = new Vector3(1, 0, 0);
            positions[i * 3 + 2] = new Vector3(0, 1, 0);
            for (var k = 0; k < 3; k++) indices[i * 3 + k] = (uint)(i * 3 + k);
        }
        var bvh = TriangleBvh.Build(positions, indices);
        await Assert.That(bvh.ItemCount).IsEqualTo(200);
        await Assert.That(bvh.Nodes.Length).IsLessThan(64);
    }

    [Test]
    public async Task one_item_per_leaf_builds_a_top_level_shape()
    {
        // The instance hierarchy: one item per leaf, so a leaf hit names exactly one instance.
        var boxes = new Aabb[37];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = new Aabb(new Vector3(i * 3f, 0f, 0f), new Vector3(i * 3f + 1f, 1f, 1f));
        var bvh = BvhBuilder.Build(boxes, maxLeafItems: 1);

        var leaves = 0;
        foreach (var node in bvh.Nodes)
            for (var child = 0; child < BvhNode.ChildCount; child++)
            {
                var meta = node.GetMeta(child);
                if (meta != BvhNode.EmptyChild && BvhNode.IsLeaf(meta))
                {
                    leaves++;
                    await Assert.That(BvhNode.LeafItemCount(meta)).IsEqualTo(1);
                }
            }
        await Assert.That(leaves).IsEqualTo(37);
    }

    [Test]
    public async Task the_build_is_deterministic()
    {
        var (positions, indices) = RandomSoup(500, seed: 9);
        var a = TriangleBvh.Build(positions, indices);
        var b = TriangleBvh.Build(positions, indices);
        await Assert.That(a.ItemOrder).IsEquivalentTo(b.ItemOrder);
        await Assert.That(a.Nodes.Length).IsEqualTo(b.Nodes.Length);
        for (var i = 0; i < a.Nodes.Length; i++)
        {
            await Assert.That(a.Nodes[i].Origin).IsEqualTo(b.Nodes[i].Origin);
            await Assert.That(a.Nodes[i].Exponents).IsEqualTo(b.Nodes[i].Exponents);
            await Assert.That(a.Nodes[i].ChildBase).IsEqualTo(b.Nodes[i].ChildBase);
            await Assert.That(a.Nodes[i].LeafBase).IsEqualTo(b.Nodes[i].LeafBase);
        }
    }
}
