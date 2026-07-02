using System.Numerics;

namespace Paradise.Physics;

internal struct DistanceResult
{
    /// <summary>Closest (or approximate deepest) point on A's full surface.</summary>
    public Vector3 ClosestA;

    /// <summary>Closest (or approximate deepest) point on B's full surface.</summary>
    public Vector3 ClosestB;

    /// <summary>Surface separation = core distance - radiusA - radiusB; negative = penetration.
    /// Exact while the core shapes stay separated; approximate once <see cref="CoresIntersect"/>.</summary>
    public float Distance;

    /// <summary>Unit direction from B's surface toward A. Never NaN.</summary>
    public Vector3 NormalBToA;

    /// <summary>True when GJK found the origin inside the Minkowski difference of the CORE shapes
    /// (deep penetration) and the result came from the analytic fallback axis instead.</summary>
    public bool CoresIntersect;
}

/// <summary>
/// GJK distance between two primitive colliders using the core-shape + radius decomposition
/// (sphere → point, capsule → segment, box → full box with radius 0). Voronoi-region simplex
/// solver per Ericson, "Real-Time Collision Detection". Allocation-free; scalar float + sqrt only.
/// </summary>
internal static class ConvexConvexDistance
{
    private const float RelativeEpsilon = 1e-8f;
    private const float OriginEpsilonSq = 1e-12f;
    private const float DuplicateEpsilonSq = 1e-14f;
    private const int MaxIterations = 64;

    private struct Vertex
    {
        public Vector3 W; // support on Minkowski difference (A - B), core shapes
        public Vector3 A; // support point on A's core
        public Vector3 B; // support point on B's core
    }

    public static DistanceResult Distance(in Collider a, in RigidTransform ta, in Collider b, in RigidTransform tb)
    {
        float radiusA = CoreRadius(a);
        float radiusB = CoreRadius(b);

        Span<Vertex> simplex = stackalloc Vertex[4];
        int count = 0;

        Vector3 initial = ta.Position - tb.Position;
        if (initial.LengthSquared() < OriginEpsilonSq) initial = Vector3.UnitX;
        simplex[count++] = Support(a, ta, b, tb, -initial);
        Vector3 v = simplex[0].W;
        bool coresIntersect = false;

        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            float vLengthSq = v.LengthSquared();
            if (vLengthSq < OriginEpsilonSq)
            {
                coresIntersect = true;
                break;
            }

            Vertex w = Support(a, ta, b, tb, -v);
            if (vLengthSq - Vector3.Dot(v, w.W) <= RelativeEpsilon * vLengthSq) break; // no progress: converged

            bool duplicate = false;
            for (int i = 0; i < count; i++)
            {
                if (Vector3.DistanceSquared(simplex[i].W, w.W) < DuplicateEpsilonSq)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate) break;

            simplex[count++] = w;
            v = ClosestOnSimplex(simplex, ref count, out bool containsOrigin);
            if (containsOrigin)
            {
                coresIntersect = true;
                break;
            }
        }

        if (coresIntersect) return PenetrationFallback(a, ta, b, tb, radiusA, radiusB);

        (Vector3 coreA, Vector3 coreB) = RecombineClosestPoints(simplex, count, v);
        float coreDistance = v.Length();
        Vector3 normal = coreDistance > 1e-6f ? v / coreDistance : FallbackAxis(a, ta, b, tb);
        return new DistanceResult
        {
            ClosestA = coreA - normal * radiusA,
            ClosestB = coreB + normal * radiusB,
            Distance = coreDistance - radiusA - radiusB,
            NormalBToA = normal,
            CoresIntersect = false,
        };
    }

    // ---- support functions -------------------------------------------------

    internal static float CoreRadius(in Collider collider) => collider.Type switch
    {
        ColliderType.Sphere => collider.Sphere.Radius,
        ColliderType.Capsule => collider.Capsule.Radius,
        _ => 0f,
    };

    internal static Vector3 SupportCore(in Collider collider, in RigidTransform transform, Vector3 worldDirection)
    {
        switch (collider.Type)
        {
            case ColliderType.Sphere:
                return transform.Position;
            case ColliderType.Capsule:
            {
                Vector3 halfAxis = transform.TransformDirection(new Vector3(0f, collider.Capsule.HalfLength, 0f));
                return Vector3.Dot(halfAxis, worldDirection) >= 0f
                    ? transform.Position + halfAxis
                    : transform.Position - halfAxis;
            }
            case ColliderType.Box:
            default:
            {
                Vector3 local = transform.InverseTransformDirection(worldDirection);
                Vector3 h = collider.Box.HalfExtents;
                var support = new Vector3(
                    local.X >= 0f ? h.X : -h.X,
                    local.Y >= 0f ? h.Y : -h.Y,
                    local.Z >= 0f ? h.Z : -h.Z);
                return transform.Transform(support);
            }
        }
    }

    private static Vertex Support(in Collider a, in RigidTransform ta, in Collider b, in RigidTransform tb, Vector3 direction)
    {
        Vector3 supportA = SupportCore(a, ta, direction);
        Vector3 supportB = SupportCore(b, tb, -direction);
        return new Vertex { W = supportA - supportB, A = supportA, B = supportB };
    }

    // ---- simplex solver ----------------------------------------------------

    private static Vector3 ClosestOnSimplex(Span<Vertex> simplex, ref int count, out bool containsOrigin)
    {
        containsOrigin = false;
        switch (count)
        {
            case 1:
                return simplex[0].W;
            case 2:
                return SolveSegment(simplex, ref count);
            case 3:
                return SolveTriangle(simplex, ref count);
            default:
                return SolveTetrahedron(simplex, ref count, out containsOrigin);
        }
    }

    private static Vector3 SolveSegment(Span<Vertex> simplex, ref int count)
    {
        Vector3 a = simplex[0].W;
        Vector3 b = simplex[1].W;
        Vector3 ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq < 1e-14f)
        {
            count = 1;
            return a;
        }

        float t = -Vector3.Dot(a, ab) / lengthSq;
        if (t <= 0f)
        {
            count = 1;
            return a;
        }
        if (t >= 1f)
        {
            simplex[0] = simplex[1];
            count = 1;
            return b;
        }

        count = 2;
        return a + ab * t;
    }

    private static Vector3 SolveTriangle(Span<Vertex> simplex, ref int count)
    {
        Vector3 a = simplex[0].W;
        Vector3 b = simplex[1].W;
        Vector3 c = simplex[2].W;

        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 ap = -a;
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f)
        {
            count = 1;
            return a;
        }

        Vector3 bp = -b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3)
        {
            simplex[0] = simplex[1];
            count = 1;
            return b;
        }

        Vector3 cp = -c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6)
        {
            simplex[0] = simplex[2];
            count = 1;
            return c;
        }

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float t = d1 / (d1 - d3);
            count = 2;
            return a + ab * t; // keep {A, B}
        }

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float t = d2 / (d2 - d6);
            simplex[1] = simplex[2]; // keep {A, C}
            count = 2;
            return a + ac * t;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
        {
            float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            simplex[0] = simplex[1]; // keep {B, C}
            simplex[1] = simplex[2];
            count = 2;
            return b + (c - b) * t;
        }

        float denom = 1f / (va + vb + vc);
        count = 3;
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    private static Vector3 SolveTetrahedron(Span<Vertex> simplex, ref int count, out bool containsOrigin)
    {
        Span<Vertex> candidate = stackalloc Vertex[4];
        Span<Vertex> best = stackalloc Vertex[4];
        int bestCount = 0;
        Vector3 bestPoint = default;
        float bestDistanceSq = float.PositiveInfinity;
        bool anyOutside = false;

        // Faces: (0,1,2) excl 3, (0,1,3) excl 2, (0,2,3) excl 1, (1,2,3) excl 0.
        Span<int> faces = stackalloc int[] { 0, 1, 2, 3, 0, 1, 3, 2, 0, 2, 3, 1, 1, 2, 3, 0 };
        for (int f = 0; f < 4; f++)
        {
            int i0 = faces[f * 4 + 0];
            int i1 = faces[f * 4 + 1];
            int i2 = faces[f * 4 + 2];
            int excluded = faces[f * 4 + 3];
            if (!OriginOutsideOfPlane(simplex[i0].W, simplex[i1].W, simplex[i2].W, simplex[excluded].W)) continue;

            anyOutside = true;
            candidate[0] = simplex[i0];
            candidate[1] = simplex[i1];
            candidate[2] = simplex[i2];
            int candidateCount = 3;
            Vector3 point = SolveTriangle(candidate, ref candidateCount);
            float distanceSq = point.LengthSquared();
            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                bestPoint = point;
                bestCount = candidateCount;
                for (int i = 0; i < candidateCount; i++) best[i] = candidate[i];
            }
        }

        if (!anyOutside)
        {
            containsOrigin = true;
            return Vector3.Zero;
        }

        containsOrigin = false;
        count = bestCount;
        for (int i = 0; i < bestCount; i++) simplex[i] = best[i];
        return bestPoint;
    }

    private static bool OriginOutsideOfPlane(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a);
        float signOrigin = Vector3.Dot(-a, normal);
        float signD = Vector3.Dot(d - a, normal);
        // Degenerate tetrahedron (coplanar) → treat as outside so the face solver runs.
        if (MathF.Abs(signD) < 1e-12f) return true;
        return signOrigin * signD < 0f;
    }

    // ---- result extraction -------------------------------------------------

    private static (Vector3 CoreA, Vector3 CoreB) RecombineClosestPoints(ReadOnlySpan<Vertex> simplex, int count, Vector3 v)
    {
        switch (count)
        {
            case 1:
                return (simplex[0].A, simplex[0].B);
            case 2:
            {
                Vector3 w0 = simplex[0].W;
                Vector3 edge = simplex[1].W - w0;
                float lengthSq = edge.LengthSquared();
                float t = lengthSq > 1e-14f ? Math.Clamp(Vector3.Dot(v - w0, edge) / lengthSq, 0f, 1f) : 0f;
                return (Vector3.Lerp(simplex[0].A, simplex[1].A, t), Vector3.Lerp(simplex[0].B, simplex[1].B, t));
            }
            default:
            {
                Vector3 v0 = simplex[1].W - simplex[0].W;
                Vector3 v1 = simplex[2].W - simplex[0].W;
                Vector3 v2 = v - simplex[0].W;
                float d00 = Vector3.Dot(v0, v0);
                float d01 = Vector3.Dot(v0, v1);
                float d11 = Vector3.Dot(v1, v1);
                float d20 = Vector3.Dot(v2, v0);
                float d21 = Vector3.Dot(v2, v1);
                float denom = d00 * d11 - d01 * d01;
                if (MathF.Abs(denom) < 1e-14f) return (simplex[0].A, simplex[0].B); // degenerate triangle
                float bv = (d11 * d20 - d01 * d21) / denom;
                float bw = (d00 * d21 - d01 * d20) / denom;
                float bu = 1f - bv - bw;
                Vector3 coreA = simplex[0].A * bu + simplex[1].A * bv + simplex[2].A * bw;
                Vector3 coreB = simplex[0].B * bu + simplex[1].B * bv + simplex[2].B * bw;
                return (coreA, coreB);
            }
        }
    }

    // ---- deep-penetration fallback (no EPA in phase 1) ----------------------

    private static DistanceResult PenetrationFallback(in Collider a, in RigidTransform ta, in Collider b, in RigidTransform tb, float radiusA, float radiusB)
    {
        Vector3 normal = FallbackAxis(a, ta, b, tb);
        Vector3 supportA = SupportCore(a, ta, -normal);
        Vector3 supportB = SupportCore(b, tb, normal);
        // Support-based gap along the axis: negative = overlap depth along this axis
        // (an upper bound of the true penetration — documented approximation).
        float gap = Vector3.Dot(normal, supportA) - Vector3.Dot(normal, supportB);
        return new DistanceResult
        {
            ClosestA = supportA - normal * radiusA,
            ClosestB = supportB + normal * radiusB,
            Distance = gap - radiusA - radiusB,
            NormalBToA = normal,
            CoresIntersect = true,
        };
    }

    /// <summary>Unit axis from B toward A used when the cores overlap or coincide. Never NaN.</summary>
    private static Vector3 FallbackAxis(in Collider a, in RigidTransform ta, in Collider b, in RigidTransform tb)
    {
        // Representative point of the other collider inside a box → axis of the nearest box face.
        if (b.Type == ColliderType.Box)
        {
            Vector3 local = tb.InverseTransform(ta.Position);
            return tb.TransformDirection(MinFaceAxis(local, b.Box.HalfExtents));
        }
        if (a.Type == ColliderType.Box)
        {
            Vector3 local = ta.InverseTransform(tb.Position);
            // Exit direction of B out of box A points from A toward B; NormalBToA is the opposite.
            return -ta.TransformDirection(MinFaceAxis(local, a.Box.HalfExtents));
        }

        // Sphere/capsule cores: axis between closest core points, +Y when coincident.
        (Vector3 coreA, Vector3 coreB) = ClosestCorePoints(a, ta, b, tb);
        Vector3 diff = coreA - coreB;
        float lengthSq = diff.LengthSquared();
        return lengthSq > 1e-12f ? diff / MathF.Sqrt(lengthSq) : Vector3.UnitY;
    }

    private static Vector3 MinFaceAxis(Vector3 localPoint, Vector3 halfExtents)
    {
        float dxPos = halfExtents.X - localPoint.X;
        float dxNeg = localPoint.X + halfExtents.X;
        float dyPos = halfExtents.Y - localPoint.Y;
        float dyNeg = localPoint.Y + halfExtents.Y;
        float dzPos = halfExtents.Z - localPoint.Z;
        float dzNeg = localPoint.Z + halfExtents.Z;

        float min = dxPos;
        Vector3 axis = Vector3.UnitX;
        if (dxNeg < min) { min = dxNeg; axis = -Vector3.UnitX; }
        if (dyPos < min) { min = dyPos; axis = Vector3.UnitY; }
        if (dyNeg < min) { min = dyNeg; axis = -Vector3.UnitY; }
        if (dzPos < min) { min = dzPos; axis = Vector3.UnitZ; }
        if (dzNeg < min) { axis = -Vector3.UnitZ; }
        return axis;
    }

    private static (Vector3 CoreA, Vector3 CoreB) ClosestCorePoints(in Collider a, in RigidTransform ta, in Collider b, in RigidTransform tb)
    {
        (Vector3 a0, Vector3 a1) = CoreSegment(a, ta);
        (Vector3 b0, Vector3 b1) = CoreSegment(b, tb);
        ClosestPoints.SegmentSegment(a0, a1, b0, b1, out Vector3 coreA, out Vector3 coreB);
        return (coreA, coreB);
    }

    private static (Vector3 P0, Vector3 P1) CoreSegment(in Collider collider, in RigidTransform transform)
    {
        if (collider.Type == ColliderType.Capsule)
        {
            Vector3 halfAxis = transform.TransformDirection(new Vector3(0f, collider.Capsule.HalfLength, 0f));
            return (transform.Position - halfAxis, transform.Position + halfAxis);
        }
        return (transform.Position, transform.Position); // sphere (and box center as degenerate)
    }
}
