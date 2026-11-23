using System.Numerics;

namespace Paradise.Physics;

/// <summary>Configures the downward ground probe of <see cref="CharacterGround"/>.</summary>
/// <remarks>The probe is a vertical ray from <see cref="StepHeight"/> above the feet to
/// <see cref="SnapDistance"/> below them, so support counts within that band. Use the constructor:
/// <c>default</c> has a filter that sees nothing.</remarks>
public struct GroundSettings
{
    /// <summary>Spacing used by <see cref="GroundSettings(float, float, float)"/> for ledge sampling.</summary>
    public const float DefaultLedgeSampleSpacing = 0.05f;

    /// <summary>Query filter of the probe; <see cref="CollisionFilter.GroundProbe"/> by default.</summary>
    public CollisionFilter Filter;

    /// <summary>Height above the feet where the probe starts.</summary>
    /// <remarks>Covers ground rising under the feet within one move (slopes and steps). A probe starting
    /// inside a ground body reports its start point, so feet sunk deeper rise by at most this much.</remarks>
    public float StepHeight;

    /// <summary>Distance below the feet within which support counts and feet snap down.</summary>
    public float SnapDistance;

    /// <summary>Smallest walkable surface normal Y, the cosine of the steepest walkable slope.</summary>
    /// <remarks>Comparisons allow 1e-5 of slack so a slope exactly at the limit stays walkable.</remarks>
    public float MinGroundNormalY;

    /// <summary>Largest planar gap between ledge samples before bisection refines the edge.</summary>
    /// <remarks>Gaps in support narrower than this can be missed; non-positive values sample only the
    /// move's end. A move is sampled at most 64 times, so long moves sample more sparsely.</remarks>
    public float LedgeSampleSpacing;

    /// <summary>Creates settings probing <see cref="CollisionFilter.GroundProbe"/>.</summary>
    /// <param name="snapDistance">See <see cref="SnapDistance"/>; non-negative.</param>
    /// <param name="stepHeight">See <see cref="StepHeight"/>; non-negative.</param>
    /// <param name="maxSlopeDegrees">Steepest walkable slope, in [0, 90] degrees.</param>
    public GroundSettings(float snapDistance, float stepHeight, float maxSlopeDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(snapDistance);
        ArgumentOutOfRangeException.ThrowIfNegative(stepHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(maxSlopeDegrees);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSlopeDegrees, 90f);
        Filter = CollisionFilter.GroundProbe;
        StepHeight = stepHeight;
        SnapDistance = snapDistance;
        MinGroundNormalY = MathF.Cos(maxSlopeDegrees * (MathF.PI / 180f));
        LedgeSampleSpacing = DefaultLedgeSampleSpacing;
    }
}

/// <summary>Result of a ground probe.</summary>
/// <remarks>When the probe reports no ground, <see cref="BodyIndex"/> is -1 if nothing was hit,
/// otherwise the fields describe the too-steep surface that was hit.</remarks>
public struct GroundHit
{
    /// <summary>World-space contact point directly below (or above) the probed feet.</summary>
    public Vector3 Position;

    /// <summary>Unit normal of the supporting surface.</summary>
    public Vector3 SurfaceNormal;

    /// <summary>Index of the supporting body in its <see cref="CollisionWorld"/>, or -1 when nothing was hit.</summary>
    public int BodyIndex;

    /// <summary>Feet height above the contact; negative when the feet are below the surface.</summary>
    public float Distance;

    internal static GroundHit None => new() { BodyIndex = -1 };
}

/// <summary>Where ground support ends along a planar move.</summary>
public struct GroundLedge
{
    /// <summary>Fraction of the planar move that stays supported, in [0, 1]; 1 when support holds throughout.</summary>
    public float SupportedFraction;

    /// <summary>Ground at the last supported point: the edge when support is lost, else the ground at the move's end.</summary>
    public GroundHit Edge;
}

/// <summary>What a grounded mover does when its move would leave ground support.</summary>
public enum LedgePolicy : byte
{
    /// <summary>Take the whole move and leave the ground; the caller decides how to fall.</summary>
    StepOff = 0,

    /// <summary>Stop at the edge.</summary>
    Stop = 1,

    /// <summary>Stop at the edge, then spend the rest of the move along X and then Z, each stopping at its own edge.</summary>
    /// <remarks>Slides along axis-aligned edges like <see cref="PlanarGroundSupport.Clamp(CollisionWorldHandle, in CollisionFilter, Vector3, Vector3, float)"/>,
    /// but reaches the edge instead of rejecting the whole axis.</remarks>
    Slide = 2,
}
