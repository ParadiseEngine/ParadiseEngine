using System.Numerics;

namespace Paradise.Geometry;

/// <summary>Möller–Trumbore ray/triangle intersection, two-sided — the same test
/// <c>Common/bvh.slang</c> performs, so a CPU reference walk and the GPU agree on what a ray hits.</summary>
public static class RayTriangle
{
    /// <summary>Returns true and the hit distance when the ray hits inside (0, <paramref name="tMax"/>).</summary>
    public static bool Intersect(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, float tMax, out float t)
    {
        t = 0f;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(direction, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        var invDet = 1f / det;
        var s = origin - a;
        var u = Vector3.Dot(s, p) * invDet;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(direction, q) * invDet;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * invDet;
        return t > 0f && t < tMax;
    }
}
