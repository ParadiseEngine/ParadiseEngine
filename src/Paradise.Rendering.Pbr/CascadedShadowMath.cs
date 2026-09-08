using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>CPU reference for parallel-split shadow maps, using the practical logarithmic/linear
/// split scheme. All matrices use the engine's right-handed, WebGPU [0,1] depth convention.</summary>
public static class CascadedShadowMath
{
    public static float Split(float near, float far, int index, int count, float lambda)
    {
        if (!float.IsFinite(near) || !float.IsFinite(far) || !float.IsFinite(lambda) ||
            !(near > 0) || !(far > near) || count is < 1 or > 4 || index < 0 || index > count)
            throw new ArgumentOutOfRangeException(nameof(near), "Require 0 < near < far, 1..4 cascades and index within 0..count.");
        var t = (float)index / count;
        return float.Lerp(near + (far - near) * t, near * MathF.Pow(far / near, t), Math.Clamp(lambda, 0, 1));
    }

    internal static (float Near, float Far) CameraRange(in Matrix4x4 projection)
    {
        if (!Matrix4x4.Invert(projection, out var inv)) return (0.1f, 100f);
        var n = Vector4.Transform(new Vector4(0, 0, 0, 1), inv);
        var f = Vector4.Transform(new Vector4(0, 0, 1, 1), inv);
        var near = MathF.Max(0.001f, -n.Z / n.W);
        var far = MathF.Abs(f.W) < 1e-7f ? 10000f : -f.Z / f.W;
        return float.IsFinite(near) && float.IsFinite(far) && far > near ? (near, far) : (0.1f, 100f);
    }

    /// <summary>Fit a stable square to the camera slice, while extending light-space depth over
    /// every caster so objects outside the camera slice can still cast into it.</summary>
    internal static Matrix4x4 Fit(PbrCamera camera, Vector3 direction, float near, float far,
        Vector3 casterCenter, Vector3 casterExtent, uint resolution, out float texelWorld, out Vector2 depthRange)
    {
        Matrix4x4.Invert(camera.View, out var inverseView);
        Matrix4x4.Invert(camera.Projection, out var inverseProjection);
        Span<Vector3> corners = stackalloc Vector3[8];
        var center = Vector3.Zero;
        for (var i = 0; i < 8; i++)
        {
            var clip = new Vector4((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, 0, 1);
            // Unproject only the near plane: infinite-far projections have w=0 at clip z=1.
            var p = Vector4.Transform(clip, inverseProjection);
            var ray = new Vector3(p.X, p.Y, p.Z) / p.W;
            var z = (i & 4) == 0 ? near : far;
            // Orthographic rays retain XY; perspective rays scale to the slice plane.
            if (MathF.Abs(camera.Projection.M34) > 0.5f) ray *= z / MathF.Max(-ray.Z, 1e-6f);
            else ray.Z = -z;
            corners[i] = Vector3.Transform(ray, inverseView);
            center += corners[i] * 0.125f;
        }
        var radius = 0f;
        foreach (var c in corners) radius = MathF.Max(radius, Vector3.Distance(c, center));
        // Quantization makes extent independent of sub-texel camera rotation error. Padding
        // covers the center's half-texel snap and the 1-pixel atlas border.
        radius = MathF.Ceiling(radius * 16f) / 16f;
        radius = MathF.Max(radius, 0.01f) * resolution / Math.Max(1u, resolution - 4);
        texelWorld = 2f * radius / resolution;
        var light = direction.LengthSquared() > 1e-6f ? Vector3.Normalize(direction) : Vector3.UnitY;
        var up = MathF.Abs(light.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(up, light));
        var planeUp = Vector3.Cross(light, right);
        center += right * (MathF.Round(Vector3.Dot(center, right) / texelWorld) * texelWorld - Vector3.Dot(center, right));
        center += planeUp * (MathF.Round(Vector3.Dot(center, planeUp) / texelWorld) * texelWorld - Vector3.Dot(center, planeUp));
        var eyeDistance = Vector3.Distance(center, casterCenter) + casterExtent.Length() + radius + 32f;
        var view = PbrMath.LookAt(center + light * eyeDistance, center, up);
        var minZ = -eyeDistance - radius;
        var maxZ = -eyeDistance + radius;
        for (var i = 0; i < 8; i++)
        {
            var c = casterCenter + new Vector3((i & 1) == 0 ? -casterExtent.X : casterExtent.X,
                (i & 2) == 0 ? -casterExtent.Y : casterExtent.Y, (i & 4) == 0 ? -casterExtent.Z : casterExtent.Z);
            var z = Vector3.Transform(c, view).Z;
            minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z);
        }
        var nearPlane = MathF.Max(0.01f, -maxZ - 16);
        var farPlane = MathF.Max(nearPlane + 1, -minZ + 16);
        depthRange = new Vector2(nearPlane, farPlane);
        return view * PbrMath.OrthographicOffCenter(-radius, radius, -radius, radius, nearPlane, farPlane);
    }
}
