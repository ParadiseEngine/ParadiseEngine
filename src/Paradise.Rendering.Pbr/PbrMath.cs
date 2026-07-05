using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>Camera/projection math for the PBR renderer — the RenderMath subset the migration
/// needs, on System.Numerics row-vector conventions (RH, Y-up, −Z forward; matrices compose
/// left-to-right: world × view × projection). .NET's projection factories already map depth to
/// WebGPU's [0, 1] clip range. Matrices upload to the GPU as raw bytes — see PbrUniforms.</summary>
public static class PbrMath
{
    /// <summary>Right-handed perspective, depth [0,1]. <paramref name="fovYRadians"/> vertical.</summary>
    public static Matrix4x4 Perspective(float fovYRadians, float aspect, float near, float far) =>
        Matrix4x4.CreatePerspectiveFieldOfView(fovYRadians, aspect, near, far);

    /// <summary>Right-handed orthographic, depth [0,1]. <paramref name="verticalSize"/> is the
    /// FULL vertical extent in world units (Godot's OrthographicSize convention: total height).</summary>
    public static Matrix4x4 Orthographic(float verticalSize, float aspect, float near, float far) =>
        Matrix4x4.CreateOrthographic(verticalSize * aspect, verticalSize, near, far);

    public static Matrix4x4 LookAt(Vector3 eye, Vector3 target, Vector3 up) =>
        Matrix4x4.CreateLookAt(eye, target, up);

    public static Matrix4x4 ViewProjection(in Matrix4x4 view, in Matrix4x4 projection) =>
        view * projection;

    /// <summary>The GPU normal matrix. WGSL needs the inverse-TRANSPOSE of the column-major
    /// model matrix; because raw System.Numerics bytes read column-major ARE the transpose,
    /// the value to upload is the plain numerics inverse — no transpose call anywhere.
    /// Falls back to the model matrix itself when singular (degenerate scale).</summary>
    public static Matrix4x4 NormalMatrix(in Matrix4x4 model) =>
        Matrix4x4.Invert(model, out var inverse) ? inverse : model;

    /// <summary>Unproject a screen pixel to a world-space ray. <paramref name="screen"/> is in
    /// pixels with the origin at the TOP-LEFT (window convention); the viewport maps to NDC
    /// x∈[−1,1] right, y∈[−1,1] up.</summary>
    public static bool TryScreenPointToRay(
        Vector2 screen, Vector2 viewportSize, in Matrix4x4 viewProjection,
        out Vector3 origin, out Vector3 direction)
    {
        origin = default;
        direction = default;
        if (viewportSize.X <= 0 || viewportSize.Y <= 0) return false;
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return false;

        var ndc = new Vector2(
            screen.X / viewportSize.X * 2f - 1f,
            1f - screen.Y / viewportSize.Y * 2f);

        var near = Vector4.Transform(new Vector4(ndc.X, ndc.Y, 0f, 1f), inverse);
        var far = Vector4.Transform(new Vector4(ndc.X, ndc.Y, 1f, 1f), inverse);
        if (MathF.Abs(near.W) < 1e-12f || MathF.Abs(far.W) < 1e-12f) return false;

        var nearPoint = new Vector3(near.X, near.Y, near.Z) / near.W;
        var farPoint = new Vector3(far.X, far.Y, far.Z) / far.W;
        var span = farPoint - nearPoint;
        var length = span.Length();
        if (length < 1e-12f) return false;

        origin = nearPoint;
        direction = span / length;
        return true;
    }
}
