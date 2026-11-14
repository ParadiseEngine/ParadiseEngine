using System.Numerics;

namespace Paradise.Rendering.Sdf;

/// <summary>CPU twins of the distance functions in <c>Common/sdf.slang</c>.</summary>
/// <remarks>The formulas, including the linear blend written as <c>a·(1−h) + b·h</c>, match the
/// shader term for term so <see cref="SdfField"/> is an oracle for what the GPU traces. Change
/// both together.</remarks>
public static class SdfMath
{
    /// <summary>The accumulated distance before any shape; exactly representable as a float.</summary>
    public const float Far = 1e9f;

    public static float Sphere(Vector3 point, float radius) => point.Length() - radius;

    public static float Box(Vector3 point, Vector3 halfExtents, float cornerRadius)
    {
        var q = Vector3.Abs(point) - (halfExtents - new Vector3(cornerRadius));
        return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f) - cornerRadius;
    }

    public static float Capsule(Vector3 point, float radius, float halfLength)
    {
        point.Y -= Math.Clamp(point.Y, -halfLength, halfLength);
        return point.Length() - radius;
    }

    public static float Cylinder(Vector3 point, float radius, float halfHeight, float edgeRadius)
    {
        var radial = MathF.Sqrt(point.X * point.X + point.Z * point.Z) - (radius - edgeRadius);
        var axial = MathF.Abs(point.Y) - (halfHeight - edgeRadius);
        var outside = new Vector2(MathF.Max(radial, 0f), MathF.Max(axial, 0f)).Length();
        return MathF.Min(MathF.Max(radial, axial), 0f) + outside - edgeRadius;
    }

    public static float Torus(Vector3 point, float ringRadius, float tubeRadius)
    {
        var ring = MathF.Sqrt(point.X * point.X + point.Z * point.Z) - ringRadius;
        return new Vector2(ring, point.Y).Length() - tubeRadius;
    }

    public static float Plane(Vector3 point) => point.Y;

    /// <summary>The local-space distance of one primitive, before its uniform scale.</summary>
    public static float Primitive(SdfShapeKind kind, Vector3 point, Vector4 size) => kind switch
    {
        SdfShapeKind.Sphere => Sphere(point, size.X),
        SdfShapeKind.Box => Box(point, new Vector3(size.X, size.Y, size.Z), size.W),
        SdfShapeKind.Capsule => Capsule(point, size.X, size.Y),
        SdfShapeKind.Cylinder => Cylinder(point, size.X, size.Y, size.W),
        SdfShapeKind.Torus => Torus(point, size.X, size.Y),
        _ => Plane(point),
    };

    /// <summary>Folds one shape's distance into the accumulated field.</summary>
    /// <param name="shapeWeight">The share of the shape's material in the result.</param>
    /// <remarks>Polynomial smooth minimum and maximum: outside the blend radius they equal the
    /// hard operation exactly, which is what lets the union skip distant shapes.</remarks>
    public static float Combine(SdfOperation operation, float accumulated, float shape, float smoothness, out float shapeWeight)
    {
        var hard = smoothness <= 0f;
        float h;
        switch (operation)
        {
            case SdfOperation.Subtraction:
                shapeWeight = 0f;
                if (hard) return MathF.Max(accumulated, -shape);
                h = Saturate(0.5f - 0.5f * (accumulated + shape) / smoothness);
                return Mix(accumulated, -shape, h) + smoothness * h * (1f - h);
            case SdfOperation.Intersection:
                if (hard)
                {
                    shapeWeight = shape > accumulated ? 1f : 0f;
                    return MathF.Max(accumulated, shape);
                }
                h = Saturate(0.5f + 0.5f * (shape - accumulated) / smoothness);
                shapeWeight = h;
                return Mix(accumulated, shape, h) + smoothness * h * (1f - h);
            default:
                if (hard)
                {
                    shapeWeight = shape < accumulated ? 1f : 0f;
                    return MathF.Min(accumulated, shape);
                }
                h = Saturate(0.5f + 0.5f * (accumulated - shape) / smoothness);
                shapeWeight = h;
                return Mix(accumulated, shape, h) - smoothness * h * (1f - h);
        }
    }

    /// <summary>The ray the renderer traces through a screen point, from the near plane.</summary>
    /// <param name="screen">Pixels from the top-left corner; a pixel's center is at +0.5.</param>
    /// <param name="length">Distance from the near plane to the far plane along the ray.</param>
    public static bool TryScreenPointToRay(
        Vector2 screen, Vector2 viewportSize, in Matrix4x4 viewProjection,
        out Vector3 origin, out Vector3 direction, out float length)
    {
        origin = default;
        direction = default;
        length = 0f;
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f) return false;
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return false;

        var ndc = new Vector2(screen.X / viewportSize.X * 2f - 1f, 1f - screen.Y / viewportSize.Y * 2f);
        var near = Vector4.Transform(new Vector4(ndc, 0f, 1f), inverse);
        var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), inverse);
        if (MathF.Abs(near.W) < 1e-12f || MathF.Abs(far.W) < 1e-12f) return false;

        origin = new Vector3(near.X, near.Y, near.Z) / near.W;
        var span = new Vector3(far.X, far.Y, far.Z) / far.W - origin;
        length = span.Length();
        if (!(length > 1e-6f)) return false;
        direction = span / length;
        return true;
    }

    internal static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
        && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
        && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

    /// <summary>Written out because a fused or rearranged lerp loses the shape's distance next to
    /// <see cref="Far"/>.</summary>
    private static float Mix(float a, float b, float h) => a * (1f - h) + b * h;

    private static float Saturate(float value) => Math.Clamp(value, 0f, 1f);
}
