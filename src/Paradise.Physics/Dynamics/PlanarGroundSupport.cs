using System.Numerics;

namespace Paradise.Physics;

/// <summary>
/// Support containment for planar movers: a position is "supported" when a downward ray from it
/// hits support geometry (e.g. the floor layer). <see cref="Clamp"/> keeps a horizontal move on
/// supported ground — accept the full move, else try each horizontal axis alone (so movers slide
/// along slab edges instead of sticking), else stay. Y is never modified.
/// </summary>
public static class PlanarGroundSupport
{
    public static bool IsSupported(CollisionWorld statics, in CollisionFilter supportFilter,
        Vector3 position, float probeDepth)
    {
        var input = new RaycastInput
        {
            Start = position,
            End = position - new Vector3(0f, probeDepth, 0f),
            Filter = supportFilter,
        };
        return statics.CastRay(input, out _);
    }

    /// <summary>Clamp the move from → to so the result stays supported. <paramref name="from"/>
    /// must itself be supported (inductively true for movers that start on the ground).</summary>
    public static Vector3 Clamp(CollisionWorld statics, in CollisionFilter supportFilter,
        Vector3 from, Vector3 to, float probeDepth)
    {
        var candidate = new Vector3(to.X, from.Y, to.Z);
        if (IsSupported(statics, supportFilter, candidate, probeDepth)) return candidate;

        var xOnly = new Vector3(to.X, from.Y, from.Z);
        if (IsSupported(statics, supportFilter, xOnly, probeDepth)) return xOnly;

        var zOnly = new Vector3(from.X, from.Y, to.Z);
        if (IsSupported(statics, supportFilter, zOnly, probeDepth)) return zOnly;

        return from;
    }
}
