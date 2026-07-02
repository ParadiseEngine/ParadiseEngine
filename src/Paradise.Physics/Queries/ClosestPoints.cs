using System.Numerics;

namespace Paradise.Physics;

/// <summary>Analytic closest-point helpers (also used as differential-test oracles for GJK).</summary>
internal static class ClosestPoints
{
    public static float PointSegmentDistanceSquared(Vector3 point, Vector3 a, Vector3 b)
        => Vector3.DistanceSquared(point, PointOnSegment(point, a, b));

    public static Vector3 PointOnSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq < 1e-12f) return a;
        float t = Math.Clamp(Vector3.Dot(point - a, ab) / lengthSq, 0f, 1f);
        return a + ab * t;
    }

    /// <summary>Closest points between segments [p1,q1] and [p2,q2] (Ericson §5.1.9).</summary>
    public static void SegmentSegment(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
    {
        Vector3 d1 = q1 - p1;
        Vector3 d2 = q2 - p2;
        Vector3 r = p1 - p2;
        float a = d1.LengthSquared();
        float e = d2.LengthSquared();
        float f = Vector3.Dot(d2, r);
        float s, t;

        const float Eps = 1e-12f;
        if (a <= Eps && e <= Eps)
        {
            c1 = p1;
            c2 = p2;
            return;
        }

        if (a <= Eps)
        {
            s = 0f;
            t = Math.Clamp(f / e, 0f, 1f);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= Eps)
            {
                t = 0f;
                s = Math.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom > Eps ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Math.Clamp(-c / a, 0f, 1f);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Math.Clamp((b - c) / a, 0f, 1f);
                }
            }
        }

        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    /// <summary>Closest point on (or in) a local-space box to a point; also reports containment.</summary>
    public static Vector3 PointOnBox(Vector3 point, Vector3 halfExtents, out bool inside)
    {
        var clamped = Vector3.Clamp(point, -halfExtents, halfExtents);
        inside = clamped == point;
        return clamped;
    }
}
