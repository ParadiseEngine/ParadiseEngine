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

    [Test]
    public async Task traced_pixels_match_the_cpu_field_in_coverage_and_material()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = TestFrame.ParityScene();

        var pixels = TestFrame.Render(backend, renderer, scene);

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

        var total = (int)(Size * Size);
        // Both classes must be well populated, or agreement would say nothing.
        await Assert.That(hits).IsGreaterThan(total / 4);
        await Assert.That(total - hits).IsGreaterThan(total / 20);
        await Assert.That(interior).IsGreaterThan(total * 3 / 4);
        // Away from silhouettes and hard seams the two pipelines may not disagree at all; on
        // them, a pixel may land either way.
        await Assert.That(interiorMismatches).IsEqualTo(0);
        await Assert.That(mismatches).IsLessThan(total / 100);
        await Assert.That(renderer.Pipeline.Find<SdfSceneFeature>()!.ShapeCount).IsEqualTo(scene.Shapes.Count);
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
