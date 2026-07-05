using System.Numerics;

namespace Paradise.Rendering.Pbr.Test;

public class PbrMathTests
{
    [Test]
    public async Task perspective_maps_near_to_depth_zero_and_far_to_one()
    {
        var projection = PbrMath.Perspective(MathF.PI / 3f, 16f / 9f, 0.1f, 100f);

        // RH camera space looks down −Z.
        var nearClip = Vector4.Transform(new Vector4(0f, 0f, -0.1f, 1f), projection);
        var farClip = Vector4.Transform(new Vector4(0f, 0f, -100f, 1f), projection);
        await Assert.That(MathF.Abs(nearClip.Z / nearClip.W)).IsLessThan(1e-5f);
        await Assert.That(MathF.Abs(farClip.Z / farClip.W - 1f)).IsLessThan(1e-5f);
    }

    [Test]
    public async Task normal_matrix_keeps_a_transformed_tangent_and_normal_perpendicular()
    {
        // Independent invariant (does not pin the implementation against itself): a tangent
        // transformed by the model's linear part and a normal transformed by NormalMatrix must
        // stay perpendicular. The model mixes rotation with non-uniform scale so the linear
        // part is non-orthogonal — a NormalMatrix missing its transpose (plain inverse) breaks
        // this for exactly such shapes, even though it agrees with the correct value for pure
        // rotations composed with uniform scale.
        var model = Matrix4x4.CreateScale(2f, 1f, 0.5f) * Matrix4x4.CreateRotationY(0.7f) *
                    Matrix4x4.CreateTranslation(3f, 4f, 5f);
        // Both vectors lie in the XZ plane, where the non-uniform scale (x=2, z=0.5) combined
        // with the Y-rotation actually distorts angles — an axis-aligned pair like (1,0,0)/(0,1,0)
        // would pass even with a buggy plain-inverse NormalMatrix, since Y is untouched by both
        // transforms here and would silently hide the bug.
        var tangent = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var normal = Vector3.Normalize(new Vector3(1f, 0f, -1f));
        await Assert.That(Vector3.Dot(tangent, normal)).IsEqualTo(0f);

        var transformedTangent = Vector3.TransformNormal(tangent, model);
        var transformedNormal = Vector3.TransformNormal(normal, PbrMath.NormalMatrix(model));

        await Assert.That(MathF.Abs(Vector3.Dot(transformedTangent, transformedNormal))).IsLessThan(1e-5f);
    }

    [Test]
    public async Task screen_center_ray_points_along_camera_forward()
    {
        var view = PbrMath.LookAt(new Vector3(0f, 2f, 10f), new Vector3(0f, 2f, 0f), Vector3.UnitY);
        var projection = PbrMath.Perspective(MathF.PI / 3f, 1.5f, 0.1f, 100f);
        var viewProjection = PbrMath.ViewProjection(view, projection);

        var ok = PbrMath.TryScreenPointToRay(
            new Vector2(300f, 200f), new Vector2(600f, 400f), viewProjection,
            out var origin, out var direction);

        await Assert.That(ok).IsTrue();
        // Camera at z=10 looking toward −Z: the center ray heads straight down −Z from near.
        await Assert.That(MathF.Abs(direction.X)).IsLessThan(1e-4f);
        await Assert.That(MathF.Abs(direction.Y)).IsLessThan(1e-4f);
        await Assert.That(direction.Z).IsLessThan(-0.999f);
        await Assert.That(MathF.Abs(origin.X)).IsLessThan(1e-4f);
        await Assert.That(MathF.Abs(origin.Y - 2f)).IsLessThan(1e-3f);
    }

    [Test]
    public async Task top_left_screen_pixel_maps_to_upper_left_ndc()
    {
        var view = PbrMath.LookAt(new Vector3(0f, 0f, 5f), Vector3.Zero, Vector3.UnitY);
        var projection = PbrMath.Perspective(MathF.PI / 2f, 1f, 0.1f, 100f);
        var viewProjection = PbrMath.ViewProjection(view, projection);

        _ = PbrMath.TryScreenPointToRay(
            Vector2.Zero, new Vector2(100f, 100f), viewProjection, out _, out var direction);

        // Window origin is top-left → NDC (−1, +1): the ray leans left and up.
        await Assert.That(direction.X).IsLessThan(0f);
        await Assert.That(direction.Y).IsGreaterThan(0f);
    }

    [Test]
    public async Task orthographic_uses_full_vertical_size()
    {
        var projection = PbrMath.Orthographic(verticalSize: 10f, aspect: 2f, near: 0.1f, far: 50f);
        // y = +5 (half of the 10-unit vertical extent) lands on the top clip plane.
        var top = Vector4.Transform(new Vector4(0f, 5f, -1f, 1f), projection);
        var right = Vector4.Transform(new Vector4(10f, 0f, -1f, 1f), projection);
        await Assert.That(MathF.Abs(top.Y - 1f)).IsLessThan(1e-5f);
        await Assert.That(MathF.Abs(right.X - 1f)).IsLessThan(1e-5f); // width = 10 × aspect 2 = 20 → half 10
    }
}
