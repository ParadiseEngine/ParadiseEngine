using System.Numerics;

namespace Paradise.Geometry;

/// <summary>Builds a deterministic quantized 8-wide BVH from a binned-SAH binary tree.</summary>
/// <remarks>Twelve bins bound split-search cost; opening the widest internal child reduces expected
/// ray visits without another SAH pass. Identical input produces identical hierarchy bytes.</remarks>
public static class BvhBuilder
{
    /// <summary>Items per leaf child the meta word's offset field can address: eight leaf children
    /// of this many items still start below offset 256.</summary>
    public const int MaxLeafItems = 31;

    private const int BinCount = 12;

    public static WideBvh Build(ReadOnlySpan<Aabb> items, int maxLeafItems = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLeafItems, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLeafItems, MaxLeafItems);

        if (items.Length == 0)
        {
            var empty = new BvhNode[1];
            SetEmptyChildren(ref empty[0], 0);
            return new WideBvh(empty, [], Aabb.Empty);
        }

        var state = new BuildState(items, maxLeafItems);
        var root = state.BuildBinary(0, items.Length);
        var nodes = new List<BvhNode>();
        var itemOrder = new List<int>(items.Length);
        nodes.Add(default);
        state.EmitWide(root, 0, nodes, itemOrder);

        var bounds = Aabb.Empty;
        foreach (var item in items) bounds.Include(item);
        return new WideBvh(nodes.ToArray(), itemOrder.ToArray(), bounds);
    }

    private struct BinaryNode
    {
        public Aabb Bounds;
        public int Left;   // -1 for a leaf
        public int Right;
        public int Start;  // leaf: first slot in the permutation
        public int Count;

        public readonly bool IsLeaf => Left < 0;
    }

    private sealed class BuildState
    {
        private readonly Aabb[] _items;
        private readonly Vector3[] _centroids;
        private readonly int[] _permutation;
        private readonly int _maxLeafItems;
        private readonly List<BinaryNode> _binary = [];

        public BuildState(ReadOnlySpan<Aabb> items, int maxLeafItems)
        {
            _items = items.ToArray();
            _centroids = new Vector3[items.Length];
            _permutation = new int[items.Length];
            for (var i = 0; i < items.Length; i++)
            {
                _centroids[i] = items[i].Center;
                _permutation[i] = i;
            }
            _maxLeafItems = maxLeafItems;
        }

        public int BuildBinary(int start, int count)
        {
            var bounds = Aabb.Empty;
            var centroidBounds = Aabb.Empty;
            for (var i = start; i < start + count; i++)
            {
                bounds.Include(in _items[_permutation[i]]);
                centroidBounds.Include(_centroids[_permutation[i]]);
            }

            var index = _binary.Count;
            _binary.Add(new BinaryNode { Bounds = bounds, Left = -1, Right = -1, Start = start, Count = count });
            if (count <= _maxLeafItems) return index;

            var split = FindSplit(start, count, in bounds, in centroidBounds);
            if (split < 0)
            {
                // Every centroid coincides (or SAH prefers one leaf): a leaf if the meta word can
                // hold it, else a median split so the depth stays bounded.
                if (count <= MaxLeafItems) return index;
                split = start + count / 2;
            }

            var left = BuildBinary(start, split - start);
            var right = BuildBinary(split, start + count - split);
            var node = _binary[index];
            node.Left = left;
            node.Right = right;
            _binary[index] = node;
            return index;
        }

        /// <summary>Partition [start, start+count) around the best binned-SAH plane and return the
        /// first index of the right half, or -1 when no split beats a leaf.</summary>
        private int FindSplit(int start, int count, in Aabb bounds, in Aabb centroidBounds)
        {
            var extent = centroidBounds.Extent;
            var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            var axisExtent = Component(extent, axis);
            if (axisExtent <= 0f) return -1;

            Span<int> binCounts = stackalloc int[BinCount];
            var binBounds = new Aabb[BinCount];
            for (var b = 0; b < BinCount; b++) binBounds[b] = Aabb.Empty;
            var axisMin = Component(centroidBounds.Min, axis);
            var toBin = BinCount / axisExtent;
            for (var i = start; i < start + count; i++)
            {
                var item = _permutation[i];
                var bin = BinOf(Component(_centroids[item], axis), axisMin, toBin);
                binCounts[bin]++;
                binBounds[bin].Include(in _items[item]);
            }

            // Sweep right-to-left accumulating the right side, then left-to-right for the left.
            Span<float> rightCost = stackalloc float[BinCount];
            var accumulated = Aabb.Empty;
            var accumulatedCount = 0;
            for (var b = BinCount - 1; b > 0; b--)
            {
                accumulated.Include(in binBounds[b]);
                accumulatedCount += binCounts[b];
                rightCost[b] = accumulatedCount == 0 ? 0f : accumulated.SurfaceArea * accumulatedCount;
            }

            var bestCost = float.PositiveInfinity;
            var bestPlane = -1;
            accumulated = Aabb.Empty;
            accumulatedCount = 0;
            for (var b = 0; b < BinCount - 1; b++)
            {
                accumulated.Include(in binBounds[b]);
                accumulatedCount += binCounts[b];
                if (accumulatedCount == 0 || accumulatedCount == count) continue;
                var cost = accumulated.SurfaceArea * accumulatedCount + rightCost[b + 1];
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestPlane = b;
                }
            }
            if (bestPlane < 0) return -1;

            // A leaf costs (its item count × its own area); splitting has to beat that, with a
            // traversal step's worth of margin, unless the leaf cannot hold the items anyway.
            var leafCost = bounds.SurfaceArea * count;
            if (count <= MaxLeafItems && bestCost + bounds.SurfaceArea >= leafCost) return -1;

            var mid = start;
            for (var i = start; i < start + count; i++)
            {
                var item = _permutation[i];
                if (BinOf(Component(_centroids[item], axis), axisMin, toBin) <= bestPlane)
                {
                    (_permutation[mid], _permutation[i]) = (_permutation[i], _permutation[mid]);
                    mid++;
                }
            }
            return mid;
        }

        private static int BinOf(float value, float axisMin, float toBin) =>
            Math.Clamp((int)((value - axisMin) * toBin), 0, BinCount - 1);

        private static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

        /// <summary>Fill <paramref name="slot"/> with the wide node for binary node
        /// <paramref name="binaryIndex"/>, appending its internal children after everything
        /// emitted so far and its leaf items to <paramref name="itemOrder"/>.</summary>
        public void EmitWide(int binaryIndex, int slot, List<BvhNode> nodes, List<int> itemOrder)
        {
            var children = new List<int>(BvhNode.ChildCount);
            var binary = _binary[binaryIndex];
            if (binary.IsLeaf) children.Add(binaryIndex);
            else
            {
                children.Add(binary.Left);
                children.Add(binary.Right);
            }

            // Grow to eight children by opening the internal child with the largest area: the
            // child a ray is most likely to enter, so opening it saves the most node visits.
            while (children.Count < BvhNode.ChildCount)
            {
                var widest = -1;
                var widestArea = -1f;
                for (var i = 0; i < children.Count; i++)
                {
                    var candidate = _binary[children[i]];
                    if (candidate.IsLeaf) continue;
                    var area = candidate.Bounds.SurfaceArea;
                    if (area > widestArea)
                    {
                        widestArea = area;
                        widest = i;
                    }
                }
                if (widest < 0) break;
                var opened = _binary[children[widest]];
                children[widest] = opened.Left;
                children.Insert(widest + 1, opened.Right);
            }

            var node = new BvhNode();
            var nodeBounds = Aabb.Empty;
            foreach (var child in children)
            {
                var childBox = _binary[child].Bounds;
                nodeBounds.Include(in childBox);
            }

            var internalCount = 0;
            foreach (var child in children)
                if (!_binary[child].IsLeaf) internalCount++;
            node.ChildBase = (uint)nodes.Count;
            for (var i = 0; i < internalCount; i++) nodes.Add(default);
            node.LeafBase = (uint)itemOrder.Count;

            // Leaf runs first, so this node's items are contiguous from LeafBase; recursing into an
            // internal child in the same loop would interleave its items with the runs after it.
            var internalSlot = 0;
            var leafOffset = 0;
            Span<Aabb> childBounds = stackalloc Aabb[BvhNode.ChildCount];
            for (var childSlot = 0; childSlot < children.Count; childSlot++)
            {
                var child = _binary[children[childSlot]];
                childBounds[childSlot] = child.Bounds;
                if (child.IsLeaf)
                {
                    node.SetMeta(childSlot, BvhNode.LeafMeta(leafOffset, child.Count));
                    for (var i = child.Start; i < child.Start + child.Count; i++) itemOrder.Add(_permutation[i]);
                    leafOffset += child.Count;
                }
                else
                {
                    node.SetMeta(childSlot, BvhNode.InternalMeta(internalSlot++));
                }
            }

            Quantize(ref node, in nodeBounds, childBounds[..children.Count]);
            SetEmptyChildren(ref node, children.Count);
            nodes[slot] = node;

            internalSlot = 0;
            foreach (var childIndex in children)
            {
                if (_binary[childIndex].IsLeaf) continue;
                EmitWide(childIndex, (int)node.ChildBase + internalSlot++, nodes, itemOrder);
            }
        }
    }

    /// <summary>Quantizes child bounds outward with power-of-two scales.</summary>
    /// <remarks>Check decoded bounds too: float rounding must not exclude a child by one ULP.</remarks>
    private static void Quantize(ref BvhNode node, in Aabb nodeBounds, ReadOnlySpan<Aabb> children)
    {
        node.Origin = nodeBounds.Min;
        var extent = nodeBounds.Extent;
        uint exponents = 0;
        Span<float> scales = stackalloc float[3];
        for (var axis = 0; axis < 3; axis++)
        {
            var e = axis switch { 0 => extent.X, 1 => extent.Y, _ => extent.Z };
            var scale = StepFor(e);
            var origin = axis switch { 0 => node.Origin.X, 1 => node.Origin.Y, _ => node.Origin.Z };
            var max = axis switch { 0 => nodeBounds.Max.X, 1 => nodeBounds.Max.Y, _ => nodeBounds.Max.Z };
            while (origin + 255f * scale < max) scale *= 2f;
            scales[axis] = scale;
            exponents |= ((BitConverter.SingleToUInt32Bits(scale) >> 23) & 0xFF) << (axis * 8);
        }
        node.Exponents = exponents;

        for (var child = 0; child < children.Length; child++)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var origin = axis switch { 0 => node.Origin.X, 1 => node.Origin.Y, _ => node.Origin.Z };
                var min = axis switch { 0 => children[child].Min.X, 1 => children[child].Min.Y, _ => children[child].Min.Z };
                var max = axis switch { 0 => children[child].Max.X, 1 => children[child].Max.Y, _ => children[child].Max.Z };
                var scale = scales[axis];

                var lo = Math.Clamp((int)MathF.Floor((min - origin) / scale), 0, 255);
                while (lo > 0 && origin + lo * scale > min) lo--;
                var hi = Math.Clamp((int)MathF.Ceiling((max - origin) / scale), 0, 255);
                while (hi < 255 && origin + hi * scale < max) hi++;

                node.SetLo(axis, child, (byte)lo);
                node.SetHi(axis, child, (byte)hi);
            }
        }
    }

    /// <summary>The smallest power of two whose 255 multiples span <paramref name="extent"/>; the
    /// smallest normal float for a flat axis, so every child's flat coordinate quantizes to 0.</summary>
    private static float StepFor(float extent)
    {
        if (!(extent > 0f)) return BitConverter.UInt32BitsToSingle(1u << 23);
        var step = extent / 255f;
        var bits = BitConverter.SingleToUInt32Bits(step);
        var exponent = (bits >> 23) & 0xFF;
        // Round the step UP to a power of two: any mantissa bits mean the next exponent.
        if ((bits & 0x7FFFFF) != 0) exponent++;
        exponent = Math.Clamp(exponent, 1u, 254u);
        return BitConverter.UInt32BitsToSingle(exponent << 23);
    }

    private static void SetEmptyChildren(ref BvhNode node, int firstEmpty)
    {
        for (var child = firstEmpty; child < BvhNode.ChildCount; child++)
        {
            node.SetMeta(child, BvhNode.EmptyChild);
            for (var axis = 0; axis < 3; axis++)
            {
                node.SetLo(axis, child, 255);
                node.SetHi(axis, child, 0);
            }
        }
    }
}
