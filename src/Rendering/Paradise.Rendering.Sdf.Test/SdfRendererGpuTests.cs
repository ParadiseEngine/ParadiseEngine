using System.Runtime.InteropServices;

namespace Paradise.Rendering.Sdf.Test;

/// <summary>Renders through a real WebGPU device and compares pixels with the CPU field and
/// with hand-computed shading; skipped where no adapter exists.</summary>
/// <remarks>Not in parallel: a software adapter traces every pixel on CPU threads, and frames
/// competing for them outlast the backend's readback wait.</remarks>
[NotInParallel]
public class SdfRendererGpuTests
{
    private const uint Size = 96;
    private const int Center = (int)Size / 2;

    /// <summary>Looks down at 45° so the ground through the view center is the world origin and
    /// world X maps linearly onto the image: pixel x = (X / 3 · ½ + ½) · Size.</summary>
    private static SdfCamera GroundCamera() => TestFrame.Orthographic(new Vector3(0f, 6f, 6f), Vector3.Zero, 6f);

    private static int GroundPixel(float worldX) => (int)((worldX / 3f * 0.5f + 0.5f) * Size);

    private static SdfMaterial Matte(float albedo) => new(new Vector3(albedo), 1f);

    /// <summary>The displayed color the CPU field predicts through a screen point of an
    /// emission-only scene.</summary>
    private static (int R, int G, int B) Expected(SdfField field, SdfScene scene, Vector2 screen, out bool hit)
    {
        hit = TestFrame.TryCpuHit(field, scene, Size, screen, out var point);
        if (!hit) return TestFrame.Encode(TestFrame.SkyBlue);
        field.Sample(point, out var material);
        return TestFrame.Encode(material.Emissive);
    }

    /// <summary>How a rendered emission-only frame compares with the CPU field, pixel by pixel.</summary>
    /// <param name="Hits">Pixels the CPU field says show a surface.</param>
    /// <param name="Interior">Pixels whose CPU answer holds across their interior: no silhouette
    /// or hard seam passes through them.</param>
    /// <param name="Mismatches">Pixels the GPU drew differently.</param>
    /// <param name="InteriorMismatches">Mismatches no rounding difference between float pipelines excuses.</param>
    private readonly record struct Agreement(int Total, int Hits, int Interior, int Mismatches, int InteriorMismatches);

    private static Agreement CompareWithCpuField(byte[] pixels, SdfScene scene)
    {
        var field = new SdfField();
        field.Set(CollectionsMarshal.AsSpan(scene.Shapes));
        var hits = 0;
        var interior = 0;
        var mismatches = 0;
        var interiorMismatches = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var center = TestFrame.PixelCenter(x, y);
                var expected = Expected(field, scene, center, out var hit);
                if (hit) hits++;
                var stable = true;
                foreach (var offset in TestFrame.Interior)
                    stable &= TestFrame.Near(Expected(field, scene, center + offset, out _), expected, 6);
                if (stable) interior++;
                if (TestFrame.Near(TestFrame.Pixel(pixels, Size, x, y), expected, 12)) continue;
                mismatches++;
                if (stable) interiorMismatches++;
            }
        }
        return new Agreement((int)(Size * Size), hits, interior, mismatches, interiorMismatches);
    }

    [Test]
    public async Task traced_pixels_match_the_cpu_field_in_coverage_and_material()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = TestFrame.ParityScene();

        var agreement = CompareWithCpuField(TestFrame.Render(backend, renderer, scene), scene);

        // Both classes must be well populated, or agreement would say nothing.
        await Assert.That(agreement.Hits).IsGreaterThan(agreement.Total / 4);
        await Assert.That(agreement.Total - agreement.Hits).IsGreaterThan(agreement.Total / 20);
        await Assert.That(agreement.Interior).IsGreaterThan(agreement.Total * 3 / 4);
        // Away from silhouettes and hard seams the two pipelines may not disagree at all; on
        // them, a pixel may land either way.
        await Assert.That(agreement.InteriorMismatches).IsEqualTo(0);
        await Assert.That(agreement.Mismatches).IsLessThan(agreement.Total / 100);
        await Assert.That(renderer.Pipeline.Find<SdfSceneFeature>()!.ShapeCount).IsEqualTo(scene.Shapes.Count);
    }

    [Test]
    public async Task rays_land_on_planes_they_never_marched_toward()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        // Low over a hard ground that runs to the far plane, where the march used to creep. The
        // renderer leaves a plane out of a ray until the ray reaches it, so everything that can
        // interrupt that is here: a crater carved below the ground, a wall plane, shapes standing
        // on it, and one that blends into it.
        var scene = TestFrame.LinearScene(TestFrame.Perspective(new Vector3(0f, 1.2f, 6f), new Vector3(0f, 0.3f, 0f), near: 0.5f));
        scene.Shapes.Add(SdfShape.Plane() with { Material = TestFrame.Emissive(0.2f, 0.2f, 0.2f) });
        scene.Shapes.Add(SdfShape.Sphere(new Vector3(-1.2f, 0f, 1.5f), 0.8f) with { Operation = SdfOperation.Subtraction });
        scene.Shapes.Add(SdfShape.Box(Matrix4x4.CreateTranslation(1.2f, 0.5f, 0.5f), new Vector3(0.5f)) with
        {
            Material = TestFrame.Emissive(0.1f, 0.8f, 0.1f),
        });
        scene.Shapes.Add(SdfShape.Sphere(new Vector3(0f, 0.6f, -1f), 0.6f) with { Material = TestFrame.Emissive(0.9f, 0.1f, 0.1f) });
        scene.Shapes.Add(new SdfShape
        {
            Kind = SdfShapeKind.Plane,
            Transform = Matrix4x4.CreateRotationZ(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(-3f, 0f, 0f),
            Material = TestFrame.Emissive(0.1f, 0.2f, 0.8f),
        });
        scene.Shapes.Add(SdfShape.Capsule(Matrix4x4.CreateTranslation(2.2f, 0.5f, -1.5f), 0.3f, 0.4f) with
        {
            Smoothness = 0.2f,
            Material = TestFrame.Emissive(0.9f, 0.8f, 0.1f),
        });

        var agreement = CompareWithCpuField(TestFrame.Render(backend, renderer, scene), scene);

        await Assert.That(agreement.Hits).IsGreaterThan(agreement.Total / 3);
        await Assert.That(agreement.Total - agreement.Hits).IsGreaterThan(agreement.Total / 20);
        await Assert.That(agreement.Interior).IsGreaterThan(agreement.Total * 3 / 4);
        await Assert.That(agreement.InteriorMismatches).IsEqualTo(0);
        await Assert.That(agreement.Mismatches).IsLessThan(agreement.Total / 100);
    }

    [Test]
    public async Task rays_near_more_shapes_than_they_can_select_fold_the_whole_list()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        // Forty spheres strung along the view axis: every ray through the middle of the frame
        // passes all of them, more than a ray's selection holds, while rays at the edge pass few.
        var scene = TestFrame.LinearScene(TestFrame.Perspective(new Vector3(0f, 0f, 5f), Vector3.Zero));
        for (var i = 0; i < 40; i++)
        {
            var center = new Vector3(0.25f * MathF.Cos(i * 0.9f), 0.25f * MathF.Sin(i * 0.9f), 2f - i * 0.15f);
            scene.Shapes.Add(SdfShape.Sphere(center, 0.3f) with
            {
                Smoothness = i % 3 == 0 ? 0.1f : 0f,
                Material = TestFrame.Emissive(0.2f + 0.7f * (i % 2), 0.2f + 0.7f * (i / 2 % 2), 0.2f + 0.7f * (i / 4 % 2)),
            });
        }

        var agreement = CompareWithCpuField(TestFrame.Render(backend, renderer, scene), scene);

        await Assert.That(agreement.Hits).IsGreaterThan(agreement.Total / 50);
        await Assert.That(agreement.InteriorMismatches).IsEqualTo(0);
        await Assert.That(agreement.Mismatches).IsLessThan(agreement.Total / 100);
    }

    [Test]
    public async Task shapes_no_ray_comes_near_leave_the_frame_untouched()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = TestFrame.LinearScene(GroundCamera());
        scene.Sky = new SdfSky { Zenith = Vector3.One, Horizon = Vector3.One, Ground = Vector3.One, AmbientIntensity = 0.3f };
        scene.Shapes.Add(SdfShape.Plane() with { Material = Matte(0.5f) });
        scene.Shapes.Add(SdfShape.Sphere(new Vector3(-0.8f, 0.7f, 0f), 0.6f) with { Smoothness = 0.3f, Material = Matte(0.7f) });
        scene.Shapes.Add(SdfShape.Box(Matrix4x4.CreateTranslation(1f, 0.5f, 0.3f), new Vector3(0.5f)) with { Material = Matte(0.3f) });
        scene.Lights.Add(new SdfLight { Direction = new Vector3(0.4f, 0.8f, 0.3f) });
        scene.Lights.Add(new SdfLight { Type = SdfLightType.Point, Position = new Vector3(0f, 2f, 1.5f), Intensity = 3f, Range = 8f });

        var alone = TestFrame.Render(backend, renderer, scene).ToArray();
        // Far down the side the sun's shadow rays lead away from: no eye, shadow or occlusion ray reaches them.
        for (var i = 0; i < 200; i++) scene.Shapes.Add(SdfShape.Sphere(new Vector3(-300f - 2f * i, 0.5f, 0f), 0.4f));
        var crowded = TestFrame.Render(backend, renderer, scene);

        await Assert.That(crowded.AsSpan().SequenceEqual(alone)).IsTrue();
        await Assert.That(renderer.Pipeline.Find<SdfSceneFeature>()!.ShapeCount).IsEqualTo(203);
    }

    [Test]
    public async Task a_lit_sphere_follows_the_engine_light_units()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = TestFrame.LinearScene(TestFrame.Perspective(new Vector3(0f, 0f, 5f), Vector3.Zero));
        scene.Shapes.Add(SdfShape.Sphere(Vector3.Zero, 1f) with { Material = Matte(0.5f) });
        var light = new SdfLight { Direction = Vector3.UnitZ, CastsShadows = false };
        scene.Lights.Add(light);

        var full = TestFrame.Render(backend, renderer, scene).ToArray();
        light.Intensity = 0.5f;
        var half = TestFrame.Render(backend, renderer, scene).ToArray();
        light.Intensity = 1f;
        scene.Shapes[0] = scene.Shapes[0] with { Material = new SdfMaterial(Vector3.One, 1f, 1f) };
        var metal = TestFrame.Render(backend, renderer, scene).ToArray();

        // Facing the light head on, the fully rough GGX lobe is D·Vis·F·π = (1/π)·¼·F·π. A matte
        // surface shows its albedo, Lambert without 1/π, plus that lobe at F = 0.04; a white metal
        // shows the lobe alone at F = 1, which a missing π would cut to a third.
        const float MatteHead = 0.5f + 0.01f;
        const float MetalHead = 0.25f;
        var center = TestFrame.Pixel(full, Size, Center, Center);
        await Assert.That(TestFrame.Near(center, TestFrame.Encode(new Vector3(MatteHead)), 3)).IsTrue();
        await Assert.That(TestFrame.Near(TestFrame.Pixel(half, Size, Center, Center), TestFrame.Encode(new Vector3(MatteHead * 0.5f)), 3)).IsTrue();
        await Assert.That(TestFrame.Near(TestFrame.Pixel(metal, Size, Center, Center), TestFrame.Encode(new Vector3(MetalHead)), 3)).IsTrue();
        // The silhouette is 17 pixels out; just inside it the surface turns away from the light.
        var limb = TestFrame.Pixel(full, Size, Center + 15, Center);
        await Assert.That(center.R - limb.R).IsGreaterThan(40);
        await Assert.That(TestFrame.Pixel(full, Size, 2, 2)).IsEqualTo(TestFrame.Encode(TestFrame.SkyBlue));
        await Assert.That(renderer.Pipeline.Find<SdfSceneFeature>()!.LightCount).IsEqualTo(1);
    }

    [Test]
    public async Task marched_shadows_darken_occluded_ground_and_follow_both_switches()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = TestFrame.LinearScene(GroundCamera());
        scene.Shapes.Add(SdfShape.Plane() with { Material = Matte(0.5f) });
        scene.Shapes.Add(SdfShape.Sphere(new Vector3(0f, 1.5f, 0f), 0.5f));
        var light = new SdfLight { Direction = Vector3.UnitY };
        scene.Lights.Add(light);
        var open = GroundPixel(2f);

        var shadowed = TestFrame.Render(backend, renderer, scene);
        var lit = TestFrame.Pixel(shadowed, Size, open, Center);
        await Assert.That(TestFrame.Pixel(shadowed, Size, Center, Center)).IsEqualTo((0, 0, 0));
        await Assert.That(lit.R).IsGreaterThan(170);

        scene.Tracing = scene.Tracing with { Shadows = false };
        var unshadowed = TestFrame.Render(backend, renderer, scene);
        await Assert.That(TestFrame.Near(TestFrame.Pixel(unshadowed, Size, Center, Center), lit, 2)).IsTrue();

        scene.Tracing = scene.Tracing with { Shadows = true };
        light.CastsShadows = false;
        var exempt = TestFrame.Render(backend, renderer, scene);
        await Assert.That(TestFrame.Near(TestFrame.Pixel(exempt, Size, Center, Center), lit, 2)).IsTrue();
    }

    [Test]
    public async Task a_low_sun_throws_a_shadow_far_from_its_caster()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        // World X maps onto the image as in GroundCamera, over 7 units either side.
        var scene = TestFrame.LinearScene(TestFrame.Orthographic(new Vector3(0f, 10f, 10f), Vector3.Zero, 14f));
        scene.Shapes.Add(SdfShape.Plane() with { Material = Matte(0.5f) });
        scene.Shapes.Add(SdfShape.Sphere(new Vector3(0f, 1f, 0f), 0.5f));
        // 12.5° above the horizon: the ray from the ground at X = -4.5 passes through the sphere's
        // center 4.6 units on, so the shadow ray has to select a shape nowhere near where it starts.
        // The shadow is an ellipse 2.3 units either side of there, starting 2.2 units from the sphere.
        scene.Lights.Add(new SdfLight { Direction = new Vector3(4.5f, 1f, 0f) });
        static int Pixel(float worldX) => (int)((worldX / 7f * 0.5f + 0.5f) * Size);

        var pixels = TestFrame.Render(backend, renderer, scene);

        await Assert.That(TestFrame.Pixel(pixels, Size, Pixel(-4.5f), Center)).IsEqualTo((0, 0, 0));
        // Lambert at that elevation is albedo x sin 12.5°, about 0.11 linear.
        await Assert.That(TestFrame.Pixel(pixels, Size, Pixel(4.5f), Center).R).IsGreaterThan(60);
        await Assert.That(TestFrame.Pixel(pixels, Size, Pixel(-1.2f), Center).R).IsGreaterThan(60);
    }

    [Test]
    public async Task a_point_light_reaches_exactly_as_far_as_its_range()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = TestFrame.LinearScene(GroundCamera());
        scene.Shapes.Add(SdfShape.Plane() with { Material = Matte(0.5f) });
        var light = new SdfLight { Type = SdfLightType.Point, Position = new Vector3(0f, 1f, 0f), Range = 2f };
        scene.Lights.Add(light);
        var beyond = GroundPixel(2.5f);

        var ranged = TestFrame.Render(backend, renderer, scene);
        await Assert.That(TestFrame.Pixel(ranged, Size, Center, Center).R).IsGreaterThan(120);
        await Assert.That(TestFrame.Pixel(ranged, Size, beyond, Center)).IsEqualTo((0, 0, 0));

        light.Range = 0f;
        var unbounded = TestFrame.Render(backend, renderer, scene);
        await Assert.That(TestFrame.Pixel(unbounded, Size, beyond, Center).R).IsGreaterThan(20);
    }

    [Test]
    public async Task ambient_occlusion_darkens_a_crease_and_leaves_open_ground_alone()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = TestFrame.LinearScene(GroundCamera());
        var white = Vector3.One;
        scene.Sky = new SdfSky { Zenith = white, Horizon = white, Ground = white };
        scene.Shapes.Add(SdfShape.Plane() with { Material = Matte(0.5f) });
        scene.Shapes.Add(SdfShape.Box(Matrix4x4.CreateTranslation(0f, 0.5f, 0f), new Vector3(0.5f)) with { Material = Matte(0.5f) });
        // Ground three centimetres from the box's wall, and ground with nothing near it.
        var crease = GroundPixel(0.53f);
        var open = GroundPixel(2.5f);

        var occluded = TestFrame.Render(backend, renderer, scene);
        scene.Tracing = scene.Tracing with { AmbientOcclusion = 0f };
        var plain = TestFrame.Render(backend, renderer, scene);

        await Assert.That(TestFrame.Pixel(plain, Size, crease, Center).R - TestFrame.Pixel(occluded, Size, crease, Center).R)
            .IsGreaterThan(20);
        await Assert.That(TestFrame.Near(TestFrame.Pixel(plain, Size, open, Center), TestFrame.Pixel(occluded, Size, open, Center), 1))
            .IsTrue();
    }

    [Test]
    public async Task the_scene_switch_presents_black_and_retracts_its_pass()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        var switches = new FeatureSwitches();
        using var renderer = new SdfRenderer(backend, switches, Size, Size);
        var scene = TestFrame.LinearScene(TestFrame.Perspective(new Vector3(0f, 0f, 5f), Vector3.Zero));
        scene.Shapes.Add(SdfShape.Sphere(Vector3.Zero, 1f) with { Material = TestFrame.Emissive(1f, 0.25f, 0f) });

        var on = TestFrame.Render(backend, renderer, scene).ToArray();
        await Assert.That(TestFrame.Near(TestFrame.Pixel(on, Size, Center, Center), TestFrame.Encode(new Vector3(1f, 0.25f, 0f)), 1)).IsTrue();
        await Assert.That(renderer.LastPassNames.SequenceEqual(["Sdf.Scene", "Sdf.Composite"])).IsTrue();

        switches.Set(SdfFeatures.Scene.Id, false);
        var off = TestFrame.Render(backend, renderer, scene);
        await Assert.That(off.Where((_, i) => i % 4 != 3).Max()).IsEqualTo((byte)0);
        await Assert.That(renderer.LastPassNames.SequenceEqual(["Sdf.Composite"])).IsTrue();
        await Assert.That(renderer.Pipeline.Find<SdfSceneFeature>()!.ShapeCount).IsEqualTo(0);

        switches.Set(SdfFeatures.Scene.Id, true);
        await Assert.That(TestFrame.Render(backend, renderer, scene).AsSpan().SequenceEqual(on)).IsTrue();
    }

    [Test]
    public async Task rejected_scenes_throw_and_leave_the_renderer_usable()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var camera = TestFrame.Perspective(new Vector3(0f, 0f, 5f), Vector3.Zero);

        var crowded = TestFrame.LinearScene(camera);
        for (var i = 0; i <= SdfRenderer.MaxShapes; i++) crowded.Shapes.Add(SdfShape.Sphere(new Vector3(i, 0f, 0f), 0.4f));
        await Assert.That(() => renderer.RenderFrame(crowded)).Throws<ArgumentException>();

        var bright = TestFrame.LinearScene(camera);
        for (var i = 0; i <= SdfRenderer.MaxLights; i++) bright.Lights.Add(new SdfLight());
        await Assert.That(() => renderer.RenderFrame(bright)).Throws<ArgumentException>();

        var stretched = TestFrame.LinearScene(camera);
        stretched.Shapes.Add(SdfShape.Box(Matrix4x4.CreateScale(1f, 3f, 1f), Vector3.One));
        await Assert.That(() => renderer.RenderFrame(stretched)).Throws<ArgumentException>();

        var blind = TestFrame.LinearScene(default);
        await Assert.That(() => renderer.RenderFrame(blind)).Throws<ArgumentException>();

        var valid = TestFrame.LinearScene(camera);
        valid.Shapes.Add(SdfShape.Sphere(Vector3.Zero, 1f) with { Material = TestFrame.Emissive(0f, 1f, 0f) });
        var pixels = TestFrame.Render(backend, renderer, valid);
        await Assert.That(TestFrame.Pixel(pixels, Size, Center, Center)).IsEqualTo((0, 255, 0));
    }

    [Test]
    public async Task resize_traces_the_same_view_at_the_new_size()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = TestFrame.LinearScene(TestFrame.Perspective(new Vector3(0f, 0f, 5f), Vector3.Zero));
        scene.Shapes.Add(SdfShape.Sphere(Vector3.Zero, 1f) with { Material = TestFrame.Emissive(1f, 0f, 0f) });
        renderer.RenderFrame(scene);

        const uint Small = 40;
        backend.Resize(Small, Small);
        renderer.Resize(Small, Small);
        renderer.RenderFrame(scene);
        var pixels = backend.ReadbackColor(out var width, out var height);

        await Assert.That(width).IsEqualTo(Small);
        await Assert.That(height).IsEqualTo(Small);
        await Assert.That(TestFrame.Pixel(pixels, Small, (int)Small / 2, (int)Small / 2)).IsEqualTo((255, 0, 0));
        await Assert.That(TestFrame.Pixel(pixels, Small, 1, 1)).IsEqualTo(TestFrame.Encode(TestFrame.SkyBlue));
        await Assert.That(renderer.AspectRatio).IsEqualTo(1f);
    }

    [Test]
    public async Task the_feature_listing_matches_what_the_renderer_declares()
    {
        var listed = new FeatureSwitches();
        SdfFeatures.DeclareAll(listed);
        await Assert.That(listed.Definitions.Count).IsEqualTo(SdfFeatures.All.Count);
        await Assert.That(listed.IsEnabled(SdfFeatures.Scene.Id)).IsTrue();

        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        await Assert.That(renderer.Pipeline.Features.Select(feature => feature.Definition).SequenceEqual(SdfFeatures.All)).IsTrue();
    }
}
