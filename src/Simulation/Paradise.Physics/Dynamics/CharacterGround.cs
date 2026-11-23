using System.Numerics;

namespace Paradise.Physics;

/// <summary>Ground queries for characters: grounded state, snapping feet to the ground and finding ledges.</summary>
/// <remarks>Support is probed by a vertical ray under the feet point (see <see cref="GroundSettings"/>), so a
/// character is over an edge once its feet point is. Walkable bodies are marked with
/// <see cref="CollisionFilter.GroundLayer"/>. Queries allocate nothing and are deterministic; an invalid
/// handle has no ground.</remarks>
public static class CharacterGround
{
    private const float SlopeTolerance = 1e-5f;
    private const int MaxLedgeSamples = 64;
    private const int EdgeBisections = 16;

    /// <summary>Probes the ground under <paramref name="feet"/>; true when walkable ground is within range.</summary>
    public static bool Probe(CollisionWorld world, in GroundSettings settings, Vector3 feet, out GroundHit ground)
        => Probe(world.Handle, settings, feet, out ground);

    /// <summary>Handle-based overload for use inside ECS systems.</summary>
    public static bool Probe(CollisionWorldHandle world, in GroundSettings settings, Vector3 feet, out GroundHit ground)
    {
        var input = new RaycastInput
        {
            Start = feet + new Vector3(0f, settings.StepHeight, 0f),
            End = feet - new Vector3(0f, settings.SnapDistance, 0f),
            Filter = settings.Filter,
        };
        if (!world.CastRay(input, out RaycastHit hit))
        {
            ground = GroundHit.None;
            return false;
        }

        ground = new GroundHit
        {
            Position = hit.Position,
            SurfaceNormal = hit.SurfaceNormal,
            BodyIndex = hit.BodyIndex,
            Distance = feet.Y - hit.Position.Y,
        };
        return hit.SurfaceNormal.Y >= settings.MinGroundNormalY - SlopeTolerance;
    }

    /// <summary>Places <paramref name="feet"/> on walkable ground within range; true when grounded.</summary>
    /// <remarks>Without ground, <paramref name="snappedFeet"/> keeps the input height so the caller decides how to fall.</remarks>
    public static bool SnapToGround(CollisionWorld world, in GroundSettings settings, Vector3 feet,
        out Vector3 snappedFeet, out GroundHit ground)
        => SnapToGround(world.Handle, settings, feet, out snappedFeet, out ground);

    /// <summary>Handle-based overload for use inside ECS systems.</summary>
    public static bool SnapToGround(CollisionWorldHandle world, in GroundSettings settings, Vector3 feet,
        out Vector3 snappedFeet, out GroundHit ground)
    {
        bool grounded = Probe(world, settings, feet, out ground);
        snappedFeet = grounded ? feet with { Y = ground.Position.Y } : feet;
        return grounded;
    }

    /// <summary>Follows the ground along the planar part of <paramref name="displacement"/>; true when support is lost.</summary>
    /// <param name="ground">The walkable ground the move starts on, as reported by <see cref="Probe(CollisionWorldHandle, in GroundSettings, Vector3, out GroundHit)"/>.</param>
    /// <remarks>Each sample probes from the previous sample's ground height, so the walk tracks slopes and
    /// steps within the probe band. The edge is refined by bisection between the last supported and the
    /// first unsupported sample.</remarks>
    public static bool FindLedge(CollisionWorld world, in GroundSettings settings, in GroundHit ground,
        Vector3 displacement, out GroundLedge ledge)
        => FindLedge(world.Handle, settings, ground, displacement, out ledge);

    /// <summary>Handle-based overload for use inside ECS systems.</summary>
    public static bool FindLedge(CollisionWorldHandle world, in GroundSettings settings, in GroundHit ground,
        Vector3 displacement, out GroundLedge ledge)
    {
        var planar = new Vector3(displacement.X, 0f, displacement.Z);
        int samples = SampleCount(planar.Length(), settings.LedgeSampleSpacing);
        float supported = 0f;
        GroundHit last = ground;
        for (int i = 1; i <= samples; i++)
        {
            float fraction = (float)i / samples;
            if (Probe(world, settings, Along(last, ground, planar, fraction), out GroundHit next))
            {
                supported = fraction;
                last = next;
                continue;
            }

            float unsupported = fraction;
            for (int step = 0; step < EdgeBisections; step++)
            {
                float middle = (supported + unsupported) * 0.5f;
                if (Probe(world, settings, Along(last, ground, planar, middle), out next))
                {
                    supported = middle;
                    last = next;
                }
                else
                {
                    unsupported = middle;
                }
            }

            ledge = new GroundLedge { SupportedFraction = supported, Edge = last };
            return true;
        }

        ledge = new GroundLedge { SupportedFraction = 1f, Edge = last };
        return false;
    }

    /// <summary>Limits the planar part of <paramref name="displacement"/> to what <paramref name="policy"/> allows
    /// from <paramref name="ground"/>, returning the planar displacement to apply.</summary>
    /// <param name="ground">The walkable ground the move starts on.</param>
    public static Vector3 ConstrainMove(CollisionWorld world, in GroundSettings settings, in GroundHit ground,
        Vector3 displacement, LedgePolicy policy)
        => ConstrainMove(world.Handle, settings, ground, displacement, policy);

    /// <summary>Handle-based overload for use inside ECS systems.</summary>
    public static Vector3 ConstrainMove(CollisionWorldHandle world, in GroundSettings settings, in GroundHit ground,
        Vector3 displacement, LedgePolicy policy)
    {
        var planar = new Vector3(displacement.X, 0f, displacement.Z);
        if (policy == LedgePolicy.StepOff || !FindLedge(world, settings, ground, planar, out GroundLedge ledge))
        {
            return planar;
        }

        Vector3 allowed = planar * ledge.SupportedFraction;
        if (policy != LedgePolicy.Slide)
        {
            return allowed;
        }

        Vector3 remaining = planar - allowed;
        GroundHit edge = ledge.Edge;
        var alongX = new Vector3(remaining.X, 0f, 0f);
        FindLedge(world, settings, edge, alongX, out GroundLedge xLedge);
        allowed += alongX * xLedge.SupportedFraction;
        var alongZ = new Vector3(0f, 0f, remaining.Z);
        FindLedge(world, settings, xLedge.Edge, alongZ, out GroundLedge zLedge);
        return allowed + alongZ * zLedge.SupportedFraction;
    }

    private static int SampleCount(float length, float spacing)
    {
        if (length <= 0f) return 0;
        if (spacing <= 0f) return 1;
        return (int)Math.Clamp(MathF.Ceiling(length / spacing), 1f, MaxLedgeSamples);
    }

    private static Vector3 Along(in GroundHit last, in GroundHit start, Vector3 planar, float fraction)
    {
        Vector3 point = start.Position + planar * fraction;
        return point with { Y = last.Position.Y };
    }
}
