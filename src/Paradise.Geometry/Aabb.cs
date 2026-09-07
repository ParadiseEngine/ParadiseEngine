using System.Numerics;

namespace Paradise.Geometry;

/// <summary>An axis-aligned box. <see cref="Empty"/> is inverted so that including anything into
/// it yields that thing's bounds.</summary>
public struct Aabb
{
    public Vector3 Min;
    public Vector3 Max;

    public Aabb(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public static Aabb Empty => new(new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    public readonly bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    public readonly Vector3 Extent => IsEmpty ? Vector3.Zero : Max - Min;

    public readonly Vector3 Center => (Min + Max) * 0.5f;

    public readonly float SurfaceArea
    {
        get
        {
            var e = Extent;
            return 2f * (e.X * e.Y + e.Y * e.Z + e.Z * e.X);
        }
    }

    public void Include(Vector3 point)
    {
        Min = Vector3.Min(Min, point);
        Max = Vector3.Max(Max, point);
    }

    public void Include(in Aabb other)
    {
        Min = Vector3.Min(Min, other.Min);
        Max = Vector3.Max(Max, other.Max);
    }

    public static Aabb Union(in Aabb a, in Aabb b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));

    /// <summary>The bounds of <paramref name="local"/> under an affine <paramref name="transform"/>
    /// (System.Numerics row-vector convention), from its eight corners.</summary>
    public static Aabb Transform(in Aabb local, in Matrix4x4 transform)
    {
        var result = Empty;
        for (var c = 0; c < 8; c++)
        {
            var corner = new Vector3(
                (c & 1) == 0 ? local.Min.X : local.Max.X,
                (c & 2) == 0 ? local.Min.Y : local.Max.Y,
                (c & 4) == 0 ? local.Min.Z : local.Max.Z);
            result.Include(Vector3.Transform(corner, transform));
        }
        return result;
    }

    /// <summary>1/direction per component, with a zero component replaced by a huge finite value
    /// the way the shader does: a genuine infinity times a zero offset is NaN, and NaN compares
    /// its way past the slab test on some GPUs and not others.</summary>
    public static Vector3 InverseDirection(Vector3 direction) => new(
        SafeInverse(direction.X), SafeInverse(direction.Y), SafeInverse(direction.Z));

    private static float SafeInverse(float d) => MathF.Abs(d) < 1e-20f ? (d < 0f ? -1e20f : 1e20f) : 1f / d;

    /// <summary>Slab test against <see cref="InverseDirection"/> of the ray. Returns the entry
    /// distance, or <see cref="float.PositiveInfinity"/> when the ray misses within (0, <paramref name="tMax"/>).</summary>
    public readonly float Intersect(Vector3 origin, Vector3 invDirection, float tMax)
    {
        var t0 = (Min - origin) * invDirection;
        var t1 = (Max - origin) * invDirection;
        var near = Vector3.Min(t0, t1);
        var far = Vector3.Max(t0, t1);
        var enter = MathF.Max(MathF.Max(near.X, near.Y), MathF.Max(near.Z, 0f));
        var exit = MathF.Min(MathF.Min(far.X, far.Y), MathF.Min(far.Z, tMax));
        return enter <= exit ? enter : float.PositiveInfinity;
    }
}
