using System.Numerics;

namespace Paradise.Geometry;

/// <summary>Builds a <see cref="WideBvh"/> over an indexed triangle list. Item <c>i</c> is the
/// triangle at indices <c>3i..3i+2</c>; <see cref="WideBvh.ItemOrder"/> is the order to lay the
/// triangles out in so each leaf's run is contiguous.</summary>
public static class TriangleBvh
{
    public const int DefaultLeafTriangles = 4;

    public static WideBvh Build(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, int maxLeafTriangles = DefaultLeafTriangles)
    {
        if (indices.Length % 3 != 0)
            throw new ArgumentException($"Index count {indices.Length} is not a multiple of three.", nameof(indices));
        var triangleCount = indices.Length / 3;
        var bounds = new Aabb[triangleCount];
        for (var t = 0; t < triangleCount; t++)
        {
            var box = Aabb.Empty;
            for (var k = 0; k < 3; k++)
            {
                var index = indices[t * 3 + k];
                if (index >= (uint)positions.Length)
                    throw new ArgumentException($"Index {index} at {t * 3 + k} is outside {positions.Length} positions.", nameof(indices));
                box.Include(positions[(int)index]);
            }
            bounds[t] = box;
        }
        return BvhBuilder.Build(bounds, maxLeafTriangles);
    }

    /// <summary>A leaf intersector over the triangle list a hierarchy was built from.</summary>
    public readonly struct Intersector(WideBvh bvh, Vector3[] positions, uint[] indices) : IBvhLeafIntersector
    {
        public bool Intersect(int slot, Vector3 origin, Vector3 direction, ref float tMax)
        {
            var triangle = bvh.ItemOrder[slot];
            var a = positions[indices[triangle * 3]];
            var b = positions[indices[triangle * 3 + 1]];
            var c = positions[indices[triangle * 3 + 2]];
            if (!RayTriangle.Intersect(origin, direction, a, b, c, tMax, out var t)) return false;
            tMax = t;
            return true;
        }
    }
}
