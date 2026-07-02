using System.Numerics;

namespace Paradise.Physics;

/// <summary>
/// Linear conservative advancement: the cast shape translates from Start to End at fixed
/// orientation; each iteration advances by the current separation over the closing rate,
/// so the shape can never tunnel through the target.
/// </summary>
internal static class ColliderCastQueries
{
    /// <summary>Separation at or below which the shapes are considered touching (meters).</summary>
    internal const float ContactEpsilon = 1e-4f;

    private const float MinApproach = 1e-9f;
    private const int MaxIterations = 32;

    public static bool Cast(in ColliderCastInput input, in Collider target, in RigidTransform targetTransform, out ColliderCastHit hit)
    {
        Vector3 displacement = input.End - input.Start;
        float fraction = 0f;
        var pose = new RigidTransform(input.Start, input.Orientation);
        DistanceResult distance = default;

        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            distance = ConvexConvexDistance.Distance(input.Collider, pose, target, targetTransform);
            if (distance.Distance <= ContactEpsilon)
            {
                // Touching or overlapping (covers a cast that starts in contact → fraction 0).
                hit = MakeHit(fraction, distance);
                return true;
            }

            float approach = -Vector3.Dot(displacement, distance.NormalBToA);
            if (approach <= MinApproach)
            {
                // Moving parallel to or away from the target: it can never be reached.
                hit = default;
                return false;
            }

            fraction += distance.Distance / approach;
            if (fraction > 1f)
            {
                hit = default;
                return false;
            }

            pose.Position = input.Start + displacement * fraction;
        }

        // Iteration cap (grazing geometry): accept the current fraction — conservative, never tunnels.
        hit = MakeHit(fraction, distance);
        return true;
    }

    private static ColliderCastHit MakeHit(float fraction, in DistanceResult distance) => new()
    {
        Fraction = fraction,
        Position = distance.ClosestB,
        SurfaceNormal = distance.NormalBToA,
        BodyIndex = -1,
    };
}
