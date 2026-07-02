using System.Numerics;

namespace Paradise.Physics;

/// <summary>Analytic per-primitive raycasts, computed in collider-local space.</summary>
internal static class RaycastQueries
{
    private const float Epsilon = 1e-12f;

    /// <summary>
    /// Casts the segment start → end against a collider at the given pose.
    /// A start point inside the collider hits at fraction 0 with the normal facing back along the ray.
    /// </summary>
    public static bool Raycast(in Collider collider, in RigidTransform transform,
        Vector3 start, Vector3 end, out float fraction, out Vector3 surfaceNormal)
    {
        Vector3 localStart = transform.InverseTransform(start);
        Vector3 localDisplacement = transform.InverseTransformDirection(end - start);

        bool hit;
        Vector3 localNormal;
        switch (collider.Type)
        {
            case ColliderType.Sphere:
                hit = RaySphere(localStart, localDisplacement, collider.Sphere.Radius, out fraction, out localNormal);
                break;
            case ColliderType.Capsule:
                hit = RayCapsule(localStart, localDisplacement, collider.Capsule.Radius, collider.Capsule.HalfLength, out fraction, out localNormal);
                break;
            case ColliderType.Box:
            default:
                hit = RayBox(localStart, localDisplacement, collider.Box.HalfExtents, out fraction, out localNormal);
                break;
        }

        if (!hit)
        {
            surfaceNormal = default;
            return false;
        }

        if (fraction < 0f)
        {
            // Started inside: report the entry point with a normal facing back along the ray.
            fraction = 0f;
            Vector3 displacement = end - start;
            float length = displacement.Length();
            surfaceNormal = length > Epsilon ? -displacement / length : Vector3.UnitY;
            return true;
        }

        surfaceNormal = transform.TransformDirection(localNormal);
        return true;
    }

    /// <summary>Negative fraction means the ray starts inside (caller converts per convention).</summary>
    private static bool RaySphere(Vector3 origin, Vector3 displacement, float radius, out float fraction, out Vector3 normal)
    {
        float c = Vector3.Dot(origin, origin) - radius * radius;
        if (c <= 0f)
        {
            fraction = -1f;
            normal = default;
            return true;
        }

        float a = Vector3.Dot(displacement, displacement);
        fraction = 0f;
        normal = default;
        if (a < Epsilon) return false;

        float b = Vector3.Dot(origin, displacement);
        if (b >= 0f) return false; // moving away

        float discriminant = b * b - a * c;
        if (discriminant < 0f) return false;

        float t = (-b - MathF.Sqrt(discriminant)) / a;
        if (t < 0f || t > 1f) return false;

        fraction = t;
        normal = (origin + displacement * t) / radius;
        return true;
    }

    private static bool RayBox(Vector3 origin, Vector3 displacement, Vector3 halfExtents, out float fraction, out Vector3 normal)
    {
        float tMin = float.NegativeInfinity;
        float tMax = float.PositiveInfinity;
        int enterAxis = -1;
        float enterSign = 0f;

        for (int axis = 0; axis < 3; axis++)
        {
            float s = Axis(origin, axis);
            float d = Axis(displacement, axis);
            float e = Axis(halfExtents, axis);
            if (MathF.Abs(d) < Epsilon)
            {
                if (s < -e || s > e)
                {
                    fraction = 0f;
                    normal = default;
                    return false;
                }
                continue;
            }

            float inv = 1f / d;
            float t1 = (-e - s) * inv;
            float t2 = (e - s) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tMin)
            {
                tMin = t1;
                enterAxis = axis;
                enterSign = d > 0f ? -1f : 1f;
            }
            if (t2 < tMax) tMax = t2;
            if (tMin > tMax)
            {
                fraction = 0f;
                normal = default;
                return false;
            }
        }

        if (tMax < 0f || tMin > 1f)
        {
            fraction = 0f;
            normal = default;
            return false;
        }

        if (tMin < 0f || enterAxis < 0)
        {
            fraction = -1f; // started inside
            normal = default;
            return true;
        }

        fraction = tMin;
        normal = enterAxis switch
        {
            0 => new Vector3(enterSign, 0f, 0f),
            1 => new Vector3(0f, enterSign, 0f),
            _ => new Vector3(0f, 0f, enterSign),
        };
        return true;
    }

    private static bool RayCapsule(Vector3 origin, Vector3 displacement, float radius, float halfLength, out float fraction, out Vector3 normal)
    {
        var segmentTop = new Vector3(0f, halfLength, 0f);
        var segmentBottom = new Vector3(0f, -halfLength, 0f);
        if (ClosestPoints.PointSegmentDistanceSquared(origin, segmentBottom, segmentTop) <= radius * radius)
        {
            fraction = -1f;
            normal = default;
            return true;
        }

        bool found = false;
        float best = float.PositiveInfinity;
        Vector3 bestNormal = default;

        // Cylinder wall (XZ plane solve), valid only within the segment's Y range.
        float a = displacement.X * displacement.X + displacement.Z * displacement.Z;
        if (a > Epsilon)
        {
            float b = origin.X * displacement.X + origin.Z * displacement.Z;
            float c = origin.X * origin.X + origin.Z * origin.Z - radius * radius;
            float discriminant = b * b - a * c;
            if (discriminant >= 0f)
            {
                float t = (-b - MathF.Sqrt(discriminant)) / a;
                if (t >= 0f && t <= 1f)
                {
                    float y = origin.Y + displacement.Y * t;
                    if (MathF.Abs(y) <= halfLength)
                    {
                        found = true;
                        best = t;
                        Vector3 p = origin + displacement * t;
                        bestNormal = new Vector3(p.X, 0f, p.Z) / radius;
                    }
                }
            }
        }

        // Cap spheres.
        foreach (float capY in stackalloc[] { halfLength, -halfLength })
        {
            var cap = new Vector3(0f, capY, 0f);
            if (RaySphere(origin - cap, displacement, radius, out float t, out Vector3 n) && t >= 0f && t < best)
            {
                found = true;
                best = t;
                bestNormal = n;
            }
        }

        fraction = best;
        normal = bestNormal;
        return found;
    }

    private static float Axis(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
}
