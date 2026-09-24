using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>Maps NDC XY and positive view depth to positions for a validated camera projection.</summary>
internal readonly record struct ViewDepthMapping(
    Vector2 OriginScale, Vector2 OriginOffset, Vector2 DepthScale, Vector2 DepthOffset)
{
    public Vector3 AtDepth(Vector2 ndc, float depth)
    {
        var origin = ndc * OriginScale + OriginOffset;
        var slope = ndc * DepthScale + DepthOffset;
        return new Vector3(origin + depth * slope, -depth);
    }

    public static bool TryCreate(in Matrix4x4 projection, out ViewDepthMapping mapping, out float near, out float far)
    {
        mapping = default;
        near = far = 0f;
        if (!IsFinite(projection) || projection.M11 == 0f || projection.M22 == 0f
            || projection.M12 != 0f || projection.M21 != 0f
            || projection.M13 != 0f || projection.M23 != 0f
            || projection.M14 != 0f || projection.M24 != 0f)
            return false;
        // Limit the contract to ordinary/off-center projections, including temporal jitter.
        if (!(projection.M34 == -1f && projection.M44 == 0f)
            && !(projection.M34 == 0f && projection.M44 == 1f))
            return false;

        var nearDepth = projection.M43 / projection.M33;
        var farDepth = (projection.M43 - projection.M44) / (projection.M33 - projection.M34);
        if (!float.IsFinite(nearDepth) || !float.IsFinite(farDepth)
            || nearDepth <= 0f || farDepth <= nearDepth || !float.IsFinite(farDepth / nearDepth))
            return false;

        // Solve clip.xy = ndc.xy * clip.w at view Z = -depth. Both camera kinds use this mapping.
        var candidate = new ViewDepthMapping(
            new Vector2(projection.M44 / projection.M11, projection.M44 / projection.M22),
            new Vector2(-projection.M41 / projection.M11, -projection.M42 / projection.M22),
            new Vector2(-projection.M34 / projection.M11, -projection.M34 / projection.M22),
            new Vector2(projection.M31 / projection.M11, projection.M32 / projection.M22));
        if (!IsFinite(candidate.AtDepth(-Vector2.One, nearDepth))
            || !IsFinite(candidate.AtDepth(Vector2.One, nearDepth))
            || !IsFinite(candidate.AtDepth(-Vector2.One, farDepth))
            || !IsFinite(candidate.AtDepth(Vector2.One, farDepth)))
            return false;

        mapping = candidate;
        near = nearDepth;
        far = farDepth;
        return true;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(in Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
        && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
