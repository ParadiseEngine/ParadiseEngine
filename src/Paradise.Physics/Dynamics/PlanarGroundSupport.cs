using System.Numerics;

namespace Paradise.Physics;

/// <summary>Keeps horizontal movement over support detected by downward rays.</summary>
/// <remarks>Clamp tries the full move, then X and Z separately, then stays put; Y is preserved.</remarks>
public static class PlanarGroundSupport
{
    public static bool IsSupported(CollisionWorld statics, in CollisionFilter supportFilter,
        Vector3 position, float probeDepth)
        => IsSupported(statics.Handle, supportFilter, position, probeDepth);

    /// <summary>Handle-based overload for use inside ECS systems.</summary>
    public static bool IsSupported(CollisionWorldHandle statics, in CollisionFilter supportFilter,
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
        => Clamp(statics.Handle, supportFilter, from, to, probeDepth);

    /// <summary>Clamps movement through an ECS-compatible handle, accepting the full move when invalid.</summary>
    public static Vector3 Clamp(CollisionWorldHandle statics, in CollisionFilter supportFilter,
        Vector3 from, Vector3 to, float probeDepth)
    {
        var candidate = new Vector3(to.X, from.Y, to.Z);
        if (!statics.IsValid) return candidate;
        if (IsSupported(statics, supportFilter, candidate, probeDepth)) return candidate;

        var xOnly = new Vector3(to.X, from.Y, from.Z);
        if (IsSupported(statics, supportFilter, xOnly, probeDepth)) return xOnly;

        var zOnly = new Vector3(from.X, from.Y, to.Z);
        if (IsSupported(statics, supportFilter, zOnly, probeDepth)) return zOnly;

        return from;
    }
}
