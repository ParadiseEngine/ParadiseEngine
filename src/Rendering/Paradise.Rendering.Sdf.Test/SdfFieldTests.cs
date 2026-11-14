namespace Paradise.Rendering.Sdf.Test;

public class SdfFieldTests
{
    private static SdfField Field(params SdfShape[] shapes)
    {
        var field = new SdfField();
        field.Set(shapes);
        return field;
    }

    [Test]
    public async Task an_empty_field_is_far_everywhere()
    {
        var field = new SdfField();
        await Assert.That(field.Count).IsEqualTo(0);
        await Assert.That(field.Distance(new Vector3(1f, 2f, 3f))).IsEqualTo(SdfMath.Far);
        await Assert.That(field.TryRaycast(Vector3.Zero, Vector3.UnitZ, 100f, out _)).IsFalse();
    }

    [Test]
    public async Task rotation_translation_and_uniform_scale_keep_true_distances()
    {
        // A unit cube turned 45° about Y, doubled and moved: its corner reaches 2√2 along X.
        var transform = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationY(MathF.PI / 4f)
            * Matrix4x4.CreateTranslation(10f, 0f, 0f);
        var field = Field(SdfShape.Box(transform, Vector3.One));

        var corner = 2f * MathF.Sqrt(2f);
        await Assert.That(field.Distance(new Vector3(10f + corner + 1.5f, 0f, 0f))).IsEqualTo(1.5f).Within(1e-4f);
        await Assert.That(field.Distance(new Vector3(10f, 5f, 0f))).IsEqualTo(3f).Within(1e-4f);
        await Assert.That(field.Distance(new Vector3(10f, 0f, 0f))).IsEqualTo(-2f).Within(1e-4f);
    }

    [Test]
    public async Task shapes_fold_in_order_so_a_subtraction_carves_only_what_precedes_it()
    {
        var cutter = SdfShape.Sphere(Vector3.Zero, 0.5f) with { Operation = SdfOperation.Subtraction };
        var carved = Field(SdfShape.Sphere(Vector3.Zero, 1f), cutter);
        var refilled = Field(cutter, SdfShape.Sphere(Vector3.Zero, 1f));

        await Assert.That(carved.Distance(Vector3.Zero)).IsEqualTo(0.5f).Within(1e-6f);
        await Assert.That(refilled.Distance(Vector3.Zero)).IsEqualTo(-1f).Within(1e-6f);
    }

    [Test]
    public async Task skipping_distant_unions_never_changes_the_distance()
    {
        var random = new Random(20261114);
        var shapes = new SdfShape[24];
        for (var i = 0; i < shapes.Length; i++)
        {
            var transform = Matrix4x4.CreateScale(0.5f + random.NextSingle())
                * Matrix4x4.CreateFromYawPitchRoll(random.NextSingle() * 6f, random.NextSingle() * 6f, random.NextSingle() * 6f)
                * Matrix4x4.CreateTranslation(Next(random, 8f));
            shapes[i] = new SdfShape
            {
                Kind = (SdfShapeKind)(i % 5),
                Transform = transform,
                Size = new Vector4(0.3f + random.NextSingle(), 0.2f + random.NextSingle() * 0.5f, 0.4f + random.NextSingle(), 0.05f),
                Operation = i % 7 == 6 ? SdfOperation.Subtraction : SdfOperation.Union,
                Smoothness = i % 3 == 0 ? 0f : random.NextSingle() * 0.6f,
            };
        }
        var field = Field(shapes);

        var worst = 0f;
        for (var sample = 0; sample < 4000; sample++)
        {
            var point = Next(random, 12f);
            worst = MathF.Max(worst, MathF.Abs(field.Distance(point) - Unskipped(field, point)));
        }
        await Assert.That(worst).IsEqualTo(0f);
    }

    [Test]
    public async Task material_blends_across_a_smooth_union_and_survives_a_subtraction()
    {
        var red = new SdfMaterial(new Vector3(1f, 0f, 0f), 0.2f, 0f, new Vector3(2f, 0f, 0f));
        var blue = new SdfMaterial(new Vector3(0f, 0f, 1f), 0.8f, 1f);
        var field = Field(
            SdfShape.Sphere(new Vector3(-1f, 0f, 0f), 1f) with { Material = red },
            SdfShape.Sphere(new Vector3(1f, 0f, 0f), 1f) with { Material = blue, Smoothness = 0.5f },
            SdfShape.Sphere(new Vector3(-1f, 1f, 0f), 0.5f) with { Operation = SdfOperation.Subtraction, Material = blue });

        field.Sample(new Vector3(0f, 0.2f, 0f), out var seam);
        await Assert.That(seam.Albedo.X).IsEqualTo(0.5f).Within(1e-5f);
        await Assert.That(seam.Albedo.Z).IsEqualTo(0.5f).Within(1e-5f);
        await Assert.That(seam.Roughness).IsEqualTo(0.5f).Within(1e-5f);
        await Assert.That(seam.Metallic).IsEqualTo(0.5f).Within(1e-5f);
        await Assert.That(seam.Emissive.X).IsEqualTo(1f).Within(1e-5f);

        // The carved wall shows the base material, not the cutter's.
        var distance = field.Sample(new Vector3(-1f, 0.5f, 0f), out var carved);
        await Assert.That(distance).IsEqualTo(0f).Within(1e-5f);
        await Assert.That(carved.Albedo).IsEqualTo(red.Albedo);
    }

    [Test]
    public async Task raycast_and_normal_find_the_surface()
    {
        var field = Field(SdfShape.Sphere(new Vector3(0f, 0f, -5f), 1f));

        var hit = field.TryRaycast(Vector3.Zero, -Vector3.UnitZ, 100f, out var distance);
        await Assert.That(hit).IsTrue();
        await Assert.That(distance).IsEqualTo(4f).Within(0.006f);
        var normal = field.Normal(new Vector3(0f, 0f, -4f));
        await Assert.That(Vector3.Distance(normal, Vector3.UnitZ)).IsLessThan(1e-3f);

        await Assert.That(field.TryRaycast(Vector3.Zero, Vector3.UnitZ, 100f, out _)).IsFalse();
        await Assert.That(field.TryRaycast(Vector3.Zero, -Vector3.UnitZ, 3f, out _)).IsFalse();
        // Grazing rays creep; a budget too small to arrive is a miss, not a false hit.
        await Assert.That(field.TryRaycast(new Vector3(0.999f, 0f, 0f), -Vector3.UnitZ, 100f, out _, maxSteps: 2)).IsFalse();
    }

    [Test]
    public async Task sizes_are_sanitized_into_what_the_shader_evaluates()
    {
        var field = Field(
            SdfShape.Box(Matrix4x4.Identity, new Vector3(1f, 2f, 3f), cornerRadius: 9f),
            SdfShape.Sphere(Vector3.Zero, -4f),
            SdfShape.Plane(2f) with { Size = new Vector4(5f) });

        await Assert.That(field.Shapes[0].Size.W).IsEqualTo(1f);
        await Assert.That(field.Shapes[1].Size.X).IsEqualTo(0f);
        await Assert.That(field.Shapes[2].Size).IsEqualTo(Vector4.Zero);
        await Assert.That(field.Shapes[0].Params).IsEqualTo(new Vector4(1f, 0f, 0f, 1f));
    }

    [Test]
    public async Task transforms_that_break_the_distance_are_rejected_and_empty_the_field()
    {
        var field = Field(SdfShape.Sphere(Vector3.Zero, 1f));
        var shear = Matrix4x4.Identity;
        shear.M21 = 0.5f;
        var invalid = new[]
        {
            Matrix4x4.CreateScale(1f, 2f, 1f),
            shear,
            Matrix4x4.CreateScale(0f),
            Matrix4x4.CreateTranslation(float.NaN, 0f, 0f),
            Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 10f),
        };
        foreach (var transform in invalid)
        {
            var shapes = new[] { SdfShape.Sphere(Vector3.Zero, 1f), SdfShape.Box(transform, Vector3.One) };
            await Assert.That(() => field.Set(shapes)).Throws<ArgumentException>();
            await Assert.That(field.Count).IsEqualTo(0);
        }
        await Assert.That(() => field.Set([SdfShape.Sphere(Vector3.Zero, float.PositiveInfinity)])).Throws<ArgumentException>();

        // A mirrored shape is still a similarity: distances hold.
        field.Set([SdfShape.Box(Matrix4x4.CreateScale(-1f, 1f, 1f), Vector3.One)]);
        await Assert.That(field.Count).IsEqualTo(1);
    }

    private static Vector3 Next(Random random, float extent) => new(
        (random.NextSingle() - 0.5f) * extent, (random.NextSingle() - 0.5f) * extent, (random.NextSingle() - 0.5f) * extent);

    /// <summary>The fold with every shape evaluated: what the bounds test must not change.</summary>
    private static float Unskipped(SdfField field, Vector3 point)
    {
        var distance = SdfMath.Far;
        foreach (ref readonly var shape in field.Shapes)
        {
            var local = Vector3.Transform(point, shape.WorldToLocal);
            var primitive = SdfMath.Primitive((SdfShapeKind)shape.Params.X, local, shape.Size) * shape.Params.W;
            distance = SdfMath.Combine((SdfOperation)shape.Params.Y, distance, primitive, shape.Params.Z, out _);
        }
        return distance;
    }
}
