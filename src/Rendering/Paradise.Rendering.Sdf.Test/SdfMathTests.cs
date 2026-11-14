namespace Paradise.Rendering.Sdf.Test;

public class SdfMathTests
{
    private const float Tolerance = 1e-5f;

    [Test]
    public async Task primitives_return_exact_distances_at_known_points()
    {
        await Assert.That(SdfMath.Sphere(new Vector3(3f, 0f, 0f), 1f)).IsEqualTo(2f).Within(Tolerance);
        await Assert.That(SdfMath.Sphere(Vector3.Zero, 1f)).IsEqualTo(-1f).Within(Tolerance);

        var half = new Vector3(1f, 2f, 3f);
        await Assert.That(SdfMath.Box(new Vector3(3f, 0f, 0f), half, 0f)).IsEqualTo(2f).Within(Tolerance);
        await Assert.That(SdfMath.Box(new Vector3(4f, 6f, 3f), half, 0f)).IsEqualTo(5f).Within(Tolerance);
        await Assert.That(SdfMath.Box(Vector3.Zero, half, 0f)).IsEqualTo(-1f).Within(Tolerance);
        // Rounding is taken out of the extents: faces stay put while corners recede.
        await Assert.That(SdfMath.Box(new Vector3(3f, 0f, 0f), half, 0.5f)).IsEqualTo(2f).Within(Tolerance);
        await Assert.That(SdfMath.Box(new Vector3(4f, 6f, 3f), half, 0.5f)).IsGreaterThan(5f);

        await Assert.That(SdfMath.Capsule(new Vector3(2f, 0f, 0f), 0.5f, 1f)).IsEqualTo(1.5f).Within(Tolerance);
        await Assert.That(SdfMath.Capsule(new Vector3(0f, 3f, 0f), 0.5f, 1f)).IsEqualTo(1.5f).Within(Tolerance);

        await Assert.That(SdfMath.Cylinder(new Vector3(3f, 0f, 0f), 1f, 2f, 0f)).IsEqualTo(2f).Within(Tolerance);
        await Assert.That(SdfMath.Cylinder(new Vector3(0f, 5f, 0f), 1f, 2f, 0f)).IsEqualTo(3f).Within(Tolerance);
        await Assert.That(SdfMath.Cylinder(new Vector3(4f, 6f, 0f), 1f, 2f, 0f)).IsEqualTo(5f).Within(Tolerance);

        await Assert.That(SdfMath.Torus(new Vector3(2f, 0f, 0f), 2f, 0.5f)).IsEqualTo(-0.5f).Within(Tolerance);
        await Assert.That(SdfMath.Torus(Vector3.Zero, 2f, 0.5f)).IsEqualTo(1.5f).Within(Tolerance);
        await Assert.That(SdfMath.Torus(new Vector3(0f, 3f, 2f), 2f, 0.5f)).IsEqualTo(2.5f).Within(Tolerance);

        await Assert.That(SdfMath.Plane(new Vector3(7f, 2f, -4f))).IsEqualTo(2f).Within(Tolerance);
        await Assert.That(SdfMath.Plane(new Vector3(7f, -2f, -4f))).IsEqualTo(-2f).Within(Tolerance);
    }

    [Test]
    public async Task hard_operations_are_min_and_max()
    {
        await Assert.That(SdfMath.Combine(SdfOperation.Union, 2f, 1f, 0f, out var weight)).IsEqualTo(1f);
        await Assert.That(weight).IsEqualTo(1f);
        await Assert.That(SdfMath.Combine(SdfOperation.Union, 1f, 2f, 0f, out weight)).IsEqualTo(1f);
        await Assert.That(weight).IsEqualTo(0f);

        // Inside both: carving leaves the outside of the cutter.
        await Assert.That(SdfMath.Combine(SdfOperation.Subtraction, -1f, -0.25f, 0f, out weight)).IsEqualTo(0.25f);
        await Assert.That(weight).IsEqualTo(0f);

        await Assert.That(SdfMath.Combine(SdfOperation.Intersection, -1f, 0.5f, 0f, out weight)).IsEqualTo(0.5f);
        await Assert.That(weight).IsEqualTo(1f);
    }

    [Test]
    public async Task smooth_operations_equal_the_hard_ones_outside_the_blend_radius()
    {
        const float k = 0.5f;
        foreach (var (a, b) in new[] { (1f, 2f), (2f, 1f), (-3f, 0.25f), (0.25f, -3f) })
        {
            await Assert.That(SdfMath.Combine(SdfOperation.Union, a, b, k, out var weight))
                .IsEqualTo(MathF.Min(a, b));
            await Assert.That(weight).IsEqualTo(b < a ? 1f : 0f);
            await Assert.That(SdfMath.Combine(SdfOperation.Intersection, a, b, k, out weight))
                .IsEqualTo(MathF.Max(a, b));
            await Assert.That(weight).IsEqualTo(b > a ? 1f : 0f);
        }
        await Assert.That(SdfMath.Combine(SdfOperation.Subtraction, 1f, 2f, k, out _)).IsEqualTo(1f);
        await Assert.That(SdfMath.Combine(SdfOperation.Subtraction, -2f, -1f, k, out _)).IsEqualTo(1f);
    }

    [Test]
    public async Task smooth_union_rounds_the_seam_and_splits_the_material_evenly()
    {
        var blended = SdfMath.Combine(SdfOperation.Union, 1f, 1f, 0.4f, out var weight);
        // Equal distances sit at the middle of the blend: k/4 below the minimum.
        await Assert.That(blended).IsEqualTo(0.9f).Within(1e-6f);
        await Assert.That(weight).IsEqualTo(0.5f);
        await Assert.That(SdfMath.Combine(SdfOperation.Intersection, 1f, 1f, 0.4f, out _)).IsEqualTo(1.1f).Within(1e-6f);
    }

    [Test]
    public async Task the_first_shape_keeps_its_distance_next_to_the_far_sentinel()
    {
        // A lerp rearranged as a + (b - a) * h would return 0 here: b vanishes beside 1e9.
        await Assert.That(SdfMath.Combine(SdfOperation.Union, SdfMath.Far, 2.5f, 0.3f, out var weight)).IsEqualTo(2.5f);
        await Assert.That(weight).IsEqualTo(1f);
        await Assert.That(SdfMath.Combine(SdfOperation.Intersection, SdfMath.Far, 2.5f, 0.3f, out _)).IsEqualTo(SdfMath.Far);
        await Assert.That(SdfMath.Combine(SdfOperation.Subtraction, SdfMath.Far, 2.5f, 0.3f, out _)).IsEqualTo(SdfMath.Far);
    }

    [Test]
    public async Task screen_center_ray_leaves_the_near_plane_along_the_view_direction()
    {
        var eye = new Vector3(1f, 2f, 5f);
        var target = new Vector3(0f, 0.5f, 0f);
        var viewProjection = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.5f, 20f);

        var found = SdfMath.TryScreenPointToRay(new Vector2(32f, 32f), new Vector2(64f, 64f), viewProjection,
            out var origin, out var direction, out var length);

        var forward = Vector3.Normalize(target - eye);
        await Assert.That(found).IsTrue();
        await Assert.That(Vector3.Distance(direction, forward)).IsLessThan(1e-4f);
        await Assert.That(Vector3.Distance(origin, eye + forward * 0.5f)).IsLessThan(1e-3f);
        await Assert.That(length).IsEqualTo(19.5f).Within(1e-2f);
        await Assert.That(SdfMath.TryScreenPointToRay(Vector2.Zero, Vector2.Zero, viewProjection, out _, out _, out _)).IsFalse();
    }
}
