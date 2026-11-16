using System.Runtime.InteropServices;

namespace Paradise.Rendering.Sdf.Test;

/// <summary>Renders the lighting features beyond direct light through a real WebGPU device and
/// compares pixels with values worked out by hand; skipped where no adapter exists.</summary>
/// <remarks>Not in parallel, for the reason <see cref="SdfRendererGpuTests"/> gives.</remarks>
[NotInParallel]
public class SdfLightingGpuTests
{
    private const uint Size = 96;
    private const int Center = (int)Size / 2;

    /// <summary>Looks down at 45° from +Z, 6 units across, so a ground point (X, 0, Z) lands at
    /// pixel (48 + 16 X, 48 + 16 Z / √2) and a point at height Y sits 16 Y / √2 pixels higher.</summary>
    private static SdfCamera GroundCamera() => TestFrame.Orthographic(new Vector3(0f, 6f, 6f), Vector3.Zero, 6f);

    private static (int X, int Y) GroundPixel(float worldX, float worldZ, float height = 0f) =>
        ((int)(Center + 16f * worldX), (int)(Center + 16f * (worldZ - height) / MathF.Sqrt(2f)));

    private static SdfMaterial Matte(float albedo) => new(new Vector3(albedo), 1f);

    private static SdfScene DarkScene(SdfCamera camera)
    {
        var scene = TestFrame.LinearScene(camera);
        scene.Sky = new SdfSky { Zenith = Vector3.Zero, Horizon = Vector3.Zero, Ground = Vector3.Zero, AmbientIntensity = 0f };
        return scene;
    }

    private static (int R, int G, int B) At(byte[] pixels, (int X, int Y) pixel) => TestFrame.Pixel(pixels, Size, pixel.X, pixel.Y);

    /// <summary>The largest difference in any channel of any pixel.</summary>
    /// <remarks>For frames drawn by two different programs that compute the same thing: a
    /// compiler is free to round the two differently, so they are compared to within a level
    /// or two rather than byte for byte.</remarks>
    private static int LargestDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var largest = a.Length == b.Length ? 0 : 255;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) largest = Math.Max(largest, Math.Abs(a[i] - b[i]));
        return largest;
    }

    /// <summary>Renders enough frames for the probes to gather every bounce that matters.</summary>
    private static byte[] Converge(WebGpuFrame frame, SdfScene scene)
    {
        byte[] pixels = [];
        for (var i = 0; i < 6; i++) pixels = TestFrame.Render(frame.Backend, frame.Renderer, scene).ToArray();
        return pixels;
    }

    private sealed record WebGpuFrame(Paradise.Rendering.WebGPU.WebGpuRenderer Backend, SdfRenderer Renderer);

    /// <summary>A wall standing on the ground, its lit face toward the camera at z = -0.75.</summary>
    private static SdfShape Wall(SdfMaterial material) =>
        SdfShape.Box(Matrix4x4.CreateTranslation(0f, 1f, -1f), new Vector3(1.5f, 1f, 0.25f), 0f) with { Material = material };

    private static SdfGi Probes() => new()
    {
        Enabled = true,
        BoundsMin = new Vector3(-3f, -0.5f, -3f),
        BoundsMax = new Vector3(3f, 2.5f, 3f),
        ProbeSpacing = 1f,
        RaysPerProbe = 64,
        // Each frame stands alone, so a handful of frames is every bounce and nothing is left
        // of the frames before.
        Hysteresis = 0f,
    };

    [Test]
    public async Task a_spot_light_lights_only_inside_its_cone()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = DarkScene(GroundCamera());
        scene.Shapes.Add(SdfShape.Plane() with { Material = Matte(0.5f) });
        // Two units up, pointing straight down: full strength within 20° of the axis, none past 30°.
        var light = new SdfLight
        {
            Type = SdfLightType.Spot,
            Position = new Vector3(0f, 2f, 0f),
            Direction = Vector3.UnitY,
            Intensity = 4f,
            InnerConeAngle = MathF.PI / 9f,
            OuterConeAngle = MathF.PI / 6f,
            CastsShadows = false,
        };
        scene.Lights.Add(light);

        var spot = TestFrame.Render(backend, renderer, scene).ToArray();
        light.Type = SdfLightType.Point;
        var point = TestFrame.Render(backend, renderer, scene);

        // Below the light: albedo x intensity / distance², plus the rough lobe seen from 45°,
        // D·Vis·F·π = (1/π)·(½ / (1 + cos 45°))·0.04·π.
        const float Below = 0.5f + 0.5f / 1.7071f * 0.04f;
        var axis = At(spot, GroundPixel(0f, 0f));
        await Assert.That(TestFrame.Near(axis, TestFrame.Encode(new Vector3(Below)), 3)).IsTrue();
        // 14° off the axis is inside the inner cone: exactly what a point light gives there.
        await Assert.That(TestFrame.Near(At(spot, GroundPixel(0.5f, 0f)), At(point, GroundPixel(0.5f, 0f)), 1)).IsTrue();
        // 25° is between the cones, 45° outside them; the point light reaches both.
        var edge = At(spot, GroundPixel(0.93f, 0f));
        await Assert.That(edge.R).IsGreaterThan(20);
        await Assert.That(edge.R).IsLessThan(At(point, GroundPixel(0.93f, 0f)).R - 20);
        await Assert.That(At(spot, GroundPixel(2f, 0f))).IsEqualTo((0, 0, 0));
        await Assert.That(At(point, GroundPixel(2f, 0f)).R).IsGreaterThan(60);
    }

    [Test]
    public async Task fog_covers_surfaces_and_sky_as_the_cpu_twin_predicts()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var emission = new Vector3(1f, 0.25f, 0f);
        var eye = new Vector3(0f, 0f, 5f);
        var scene = TestFrame.LinearScene(TestFrame.Perspective(eye, Vector3.Zero));
        scene.Shapes.Add(SdfShape.Sphere(Vector3.Zero, 1f) with { Material = TestFrame.Emissive(emission.X, emission.Y, emission.Z) });
        var field = new SdfField();
        field.Set(CollectionsMarshal.AsSpan(scene.Shapes));

        // Uniform fog: the sphere's near pole is 4 units from the eye, the sky at the far distance.
        var fog = new SdfFog { Enabled = true, Color = new Vector3(0.2f, 0.6f, 0.2f), Density = 0.2f, MaxOpacity = 0.75f };
        scene.Fog = fog;
        var uniform = TestFrame.Render(backend, renderer, scene).ToArray();
        await Assert.That(TestFrame.TryCpuHit(field, scene, Size, TestFrame.PixelCenter(Center, Center), out var hit)).IsTrue();
        var opacity = SdfMath.FogOpacity(fog, eye, Vector3.Normalize(hit - eye), Vector3.Distance(hit, eye));
        await Assert.That(opacity).IsGreaterThan(0.5f);
        await Assert.That(opacity).IsLessThan(0.6f);
        await Assert.That(TestFrame.Near(TestFrame.Pixel(uniform, Size, Center, Center), TestFrame.Encode(Vector3.Lerp(emission, fog.Color, opacity)), 2)).IsTrue();
        await Assert.That(TestFrame.Near(TestFrame.Pixel(uniform, Size, 2, 2), TestFrame.Encode(Vector3.Lerp(TestFrame.SkyBlue, fog.Color, 0.75f)), 2)).IsTrue();

        // Fog that thins with height and starts away from the eye, seen along a slanted ray.
        eye = new Vector3(0f, 4f, 5f);
        scene.Camera = TestFrame.Perspective(eye, Vector3.Zero);
        fog = fog with { Density = 0.5f, HeightFalloff = 0.7f, BaseHeight = 0.5f, StartDistance = 1.5f, MaxOpacity = 1f };
        scene.Fog = fog;
        var layered = TestFrame.Render(backend, renderer, scene).ToArray();
        await Assert.That(TestFrame.TryCpuHit(field, scene, Size, TestFrame.PixelCenter(Center, Center), out hit)).IsTrue();
        opacity = SdfMath.FogOpacity(fog, eye, Vector3.Normalize(hit - eye), Vector3.Distance(hit, eye));
        await Assert.That(opacity).IsGreaterThan(0.15f);
        await Assert.That(opacity).IsLessThan(0.85f);
        await Assert.That(TestFrame.Near(TestFrame.Pixel(layered, Size, Center, Center), TestFrame.Encode(Vector3.Lerp(emission, fog.Color, opacity)), 2)).IsTrue();

        // The sky is fogged at the far plane, where the march gave up, not at MaxDistance (200),
        // so it matches a surface clipped there. Thin fog, so the two would differ.
        fog = new SdfFog { Enabled = true, Color = fog.Color, Density = 0.015f };
        scene.Fog = fog;
        var thin = TestFrame.Render(backend, renderer, scene).ToArray();
        await Assert.That(SdfMath.TryScreenPointToRay(TestFrame.PixelCenter(2, 2), new Vector2(Size),
            scene.Camera.View * scene.Camera.Projection, out var nearPoint, out var skyward, out var span)).IsTrue();
        var farPlane = SdfMath.FogOpacity(fog, eye, skyward, Vector3.Distance(eye, nearPoint) + span);
        // About 0.43 at the far plane, against 0.95 at MaxDistance.
        await Assert.That(farPlane).IsLessThan(0.5f);
        await Assert.That(TestFrame.Near(TestFrame.Pixel(thin, Size, 2, 2), TestFrame.Encode(Vector3.Lerp(TestFrame.SkyBlue, fog.Color, farPlane)), 2)).IsTrue();

        scene.Fog = fog with { Enabled = false };
        var clear = TestFrame.Render(backend, renderer, scene);
        await Assert.That(TestFrame.Near(TestFrame.Pixel(clear, Size, Center, Center), TestFrame.Encode(emission), 1)).IsTrue();
    }

    [Test]
    public async Task a_mirror_shows_the_scene_only_where_reflections_are_traced()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = TestFrame.LinearScene(GroundCamera());
        scene.Sky = scene.Sky with { AmbientIntensity = 1f };
        // A mirror floor; the roughness is raised to the renderer's minimum of 0.03.
        scene.Shapes.Add(SdfShape.Plane() with { Material = new SdfMaterial(Vector3.One, 0f, 1f) });
        scene.Shapes.Add(SdfShape.Sphere(new Vector3(0f, 1.5f, 0f), 0.5f) with { Material = TestFrame.Emissive(1f, 0f, 0f) });
        // The view descends at 45°, so the floor at z = 1.5 mirrors it up through the sphere's center.
        var image = GroundPixel(0f, 1.5f);
        var beside = GroundPixel(-2f, 1.5f);
        var sky = TestFrame.Encode(TestFrame.SkyBlue);

        var untraced = TestFrame.Render(backend, renderer, scene).ToArray();
        await Assert.That(TestFrame.Near(At(untraced, image), sky, 2)).IsTrue();

        scene.Reflections = new SdfReflections { Enabled = true };
        var traced = TestFrame.Render(backend, renderer, scene).ToArray();
        // 97% the sphere's emission and 3% the blue irradiance a rough surface would blur toward.
        await Assert.That(TestFrame.Near(At(traced, image), TestFrame.Encode(new Vector3(0.97f, 0f, 0.03f)), 3)).IsTrue();
        await Assert.That(TestFrame.Near(At(traced, beside), sky, 2)).IsTrue();
        await Assert.That(At(traced, GroundPixel(0f, 0f, 1.5f)).R).IsEqualTo(255);

        // A floor rougher than the limit keeps the sky.
        scene.Reflections = scene.Reflections with { MaxRoughness = 0.01f };
        // The same picture from two programs: the one that can trace reflections, and the plain one.
        var limited = TestFrame.Render(backend, renderer, scene);
        await Assert.That(LargestDifference(limited, untraced)).IsLessThanOrEqualTo(2);
        var feature = renderer.Pipeline.Find<SdfSceneFeature>()!;
        await Assert.That(feature.UsedReflections).IsTrue();
        scene.Reflections = scene.Reflections with { Enabled = false };
        await Assert.That(TestFrame.Render(backend, renderer, scene).AsSpan().SequenceEqual(untraced)).IsTrue();
        await Assert.That(feature.UsedReflections).IsFalse();
    }

    [Test]
    public async Task a_mirror_shows_surfaces_lit_shadowed_and_under_the_probes_as_they_are()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var frame = new WebGpuFrame(backend, renderer);
        var scene = DarkScene(GroundCamera());
        scene.Reflections = new SdfReflections { Enabled = true };
        scene.Shapes.Add(SdfShape.Plane() with { Material = new SdfMaterial(Vector3.One, 0f, 1f) });
        scene.Shapes.Add(Wall(Matte(1f)));
        var light = new SdfLight
        {
            Type = SdfLightType.Spot,
            Position = new Vector3(0f, 1f, 3f),
            Direction = Vector3.UnitZ,
            Intensity = 8f,
            InnerConeAngle = 10f * MathF.PI / 180f,
            OuterConeAngle = 14f * MathF.PI / 180f,
        };
        scene.Lights.Add(light);
        // The floor at z = 0.25 mirrors the view up to the wall's face at height 1, the middle
        // of the beam, 3.75 from the light.
        var image = GroundPixel(0f, 0.25f);
        const float Lit = 8f / (3.75f * 3.75f);

        // A white wall seen in a mirror shows the light it receives, without a highlight.
        var lit = At(TestFrame.Render(backend, renderer, scene), image);
        await Assert.That(TestFrame.Near(lit, TestFrame.Encode(new Vector3(0.97f * Lit)), 4)).IsTrue();

        // A black card across the beam, clear of the view and of the mirrored ray, shadows the
        // wall, and its image with it unless reflections are told not to look.
        scene.Shapes.Add(SdfShape.Box(Matrix4x4.CreateTranslation(0f, 1f, 2.2f), new Vector3(0.3f, 0.3f, 0.05f), 0f) with { Material = Matte(0f) });
        await Assert.That(At(TestFrame.Render(backend, renderer, scene), image)).IsEqualTo((0, 0, 0));
        scene.Reflections = scene.Reflections with { Shadows = false };
        await Assert.That(TestFrame.Near(At(TestFrame.Render(backend, renderer, scene), image), lit, 1)).IsTrue();

        // Under a grey sky alone the mirrored wall shows the ambient light it receives: all of
        // the sky without probes, and with probes that return no bounce the half above the floor.
        scene.Lights.Clear();
        var grey = new Vector3(0.5f);
        scene.Sky = new SdfSky { Zenith = grey, Horizon = grey, Ground = grey, AmbientIntensity = 1f };
        var underSky = At(TestFrame.Render(backend, renderer, scene), image);
        await Assert.That(TestFrame.Near(underSky, TestFrame.Encode(new Vector3(0.97f * 0.5f + 0.03f * 0.5f)), 3)).IsTrue();
        scene.Gi = Probes() with { Bounce = 0f };
        var underProbes = At(Converge(frame, scene), image);
        await Assert.That(underProbes.R).IsLessThan(underSky.R - 25);
        await Assert.That(underProbes.R).IsGreaterThan(100);
    }

    [Test]
    public async Task probes_occlude_the_sky_and_return_what_the_ground_bounces()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var frame = new WebGpuFrame(backend, renderer);
        // A sky of radiance 0.5 in every direction over grey ground, and nothing else lit.
        var scene = TestFrame.LinearScene(GroundCamera());
        var grey = new Vector3(0.5f);
        scene.Sky = new SdfSky { Zenith = grey, Horizon = grey, Ground = grey, AmbientIntensity = 1f };
        scene.Tracing = scene.Tracing with { AmbientOcclusion = 0f };
        scene.Shapes.Add(SdfShape.Plane() with { Material = Matte(0.5f) });
        scene.Shapes.Add(Wall(Matte(0.5f)));
        var wall = GroundPixel(0f, -0.75f, 1f);
        var open = GroundPixel(0f, 2.5f);

        // Under the bare sky every matte surface shows albedo plus the rough lobe's 0.04, times 0.5.
        var plain = TestFrame.Render(backend, renderer, scene).ToArray();
        var unoccluded = TestFrame.Encode(new Vector3(0.54f * 0.5f));
        await Assert.That(TestFrame.Near(At(plain, wall), unoccluded, 2)).IsTrue();
        await Assert.That(TestFrame.Near(At(plain, open), unoccluded, 2)).IsTrue();

        // The wall's face sees sky above the horizon and ground below it. The ground returns half
        // of the sky it receives, so the face receives (1 + 0.5) / 2 of the sky.
        scene.Gi = Probes();
        var bounced = Converge(frame, scene);
        var withBounce = At(bounced, wall);
        // Measured 2 levels off: here the half-space figure is nearly exact, and tight enough
        // that a probe inside the wall, which holds nothing, would show if it were counted.
        await Assert.That(TestFrame.Near(withBounce, TestFrame.Encode(new Vector3(0.54f * 0.5f * 0.75f)), 5)).IsTrue();
        // Open ground still sees nearly the whole sky.
        await Assert.That(TestFrame.Near(At(bounced, open), unoccluded, 8)).IsTrue();

        // Weighed by position alone, the probes inside the wall would count if they were not
        // marked invalid. They hold nothing, and would darken the face.
        scene.Gi = scene.Gi with { Visibility = false };
        var unweighed = At(Converge(frame, scene), wall);
        await Assert.That(TestFrame.Near(unweighed, withBounce, 6)).IsTrue();
        scene.Gi = scene.Gi with { Visibility = true };

        // Without the bounce the ground is black to the probes and the face receives half the sky.
        scene.Gi = scene.Gi with { Bounce = 0f };
        var single = Converge(frame, scene);
        // The face is finite, so sky shows around it too and the half-space figure holds roughly.
        await Assert.That(TestFrame.Near(At(single, wall), TestFrame.Encode(new Vector3(0.54f * 0.5f * 0.5f)), 10)).IsTrue();
        await Assert.That(At(single, wall).R).IsLessThan(withBounce.R - 10);
    }

    [Test]
    public async Task probes_carry_a_lit_walls_color_onto_ground_the_light_misses()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var frame = new WebGpuFrame(backend, renderer);
        var scene = DarkScene(GroundCamera());
        scene.Shapes.Add(SdfShape.Plane() with { Material = Matte(0.5f) });
        scene.Shapes.Add(Wall(new SdfMaterial(new Vector3(1f, 0f, 0f), 1f)));
        // A beam along the ground that ends on the wall's face: its cone is 0.93 wide there and
        // would not come down to the ground until behind the wall.
        scene.Lights.Add(new SdfLight
        {
            Type = SdfLightType.Spot,
            Position = new Vector3(0f, 1f, 3f),
            Direction = Vector3.UnitZ,
            Intensity = 64f,
            InnerConeAngle = 10f * MathF.PI / 180f,
            OuterConeAngle = 14f * MathF.PI / 180f,
        });
        var near = GroundPixel(0f, 0f);
        var far = GroundPixel(0f, 2.5f);

        var direct = TestFrame.Render(backend, renderer, scene).ToArray();
        await Assert.That(At(direct, near)).IsEqualTo((0, 0, 0));
        await Assert.That(At(direct, GroundPixel(0f, -0.75f, 1f)).R).IsGreaterThan(250);

        scene.Gi = Probes();
        var bounced = Converge(frame, scene);
        var close = At(bounced, near);
        await Assert.That(close.R).IsGreaterThan(40);
        await Assert.That(close.G + close.B).IsEqualTo(0);
        await Assert.That(At(bounced, far).R).IsLessThan(close.R - 15);
        // Ground behind the wall is lit by nothing the light reaches.
        await Assert.That(At(bounced, GroundPixel(2.5f, -2.5f)).R).IsLessThan(close.R / 2);

        // A polished floor that traces nothing still mirrors the wall's light, from the probes:
        // their light from the side the mirror direction points to, up and toward the wall. That
        // is several times what its 3% of blur toward the irradiance would show on its own.
        scene.Shapes[0] = SdfShape.Plane() with { Material = new SdfMaterial(Vector3.One, 0f, 1f) };
        var polished = At(Converge(frame, scene), near);
        await Assert.That(polished.R).IsGreaterThan(200);
        await Assert.That(polished.G + polished.B).IsEqualTo(0);
        scene.Gi = scene.Gi with { Enabled = false };
        await Assert.That(At(TestFrame.Render(backend, renderer, scene), near)).IsEqualTo((0, 0, 0));
        scene.Gi = scene.Gi with { Enabled = true };
        scene.Shapes[0] = SdfShape.Plane() with { Material = Matte(0.5f) };

        // A wall in shadow has nothing to bounce: a black card across the beam, clear of the view.
        scene.Shapes.Add(SdfShape.Box(Matrix4x4.CreateTranslation(0f, 1f, 2.2f), new Vector3(0.3f, 0.3f, 0.05f), 0f) with { Material = Matte(0f) });
        var shadowed = Converge(frame, scene);
        await Assert.That(At(shadowed, near)).IsEqualTo((0, 0, 0));
    }

    /// <summary>The pixel a world point lands on.</summary>
    private static (int X, int Y) Project(SdfScene scene, Vector3 point)
    {
        var clip = Vector4.Transform(new Vector4(point, 1f), scene.Camera.View * scene.Camera.Projection);
        return ((int)((clip.X / clip.W * 0.5f + 0.5f) * Size), (int)((0.5f - clip.Y / clip.W * 0.5f) * Size));
    }

    [Test]
    public async Task probe_light_does_not_leak_through_a_wall_while_visibility_is_weighed()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var frame = new WebGpuFrame(backend, renderer);
        // Seen from behind the wall. Its lit face is at z = -1.2, a fifth of a cell behind the
        // probes at z = -1, which are bright; the ground behind it lies between them and the dark
        // probes at z = -2.
        var scene = DarkScene(TestFrame.Orthographic(new Vector3(0f, 6f, -6f), Vector3.Zero, 6f));
        scene.Shapes.Add(SdfShape.Plane() with { Material = Matte(0.5f) });
        scene.Shapes.Add(SdfShape.Box(Matrix4x4.CreateTranslation(0f, 1f, -1.4f), new Vector3(2.5f, 1f, 0.2f), 0f) with { Material = Matte(1f) });
        scene.Lights.Add(new SdfLight
        {
            Type = SdfLightType.Spot,
            Position = new Vector3(0f, 1f, 3f),
            Direction = Vector3.UnitZ,
            Intensity = 64f,
            InnerConeAngle = 8f * MathF.PI / 180f,
            OuterConeAngle = 12f * MathF.PI / 180f,
        });
        var behind = Project(scene, new Vector3(0f, 0f, -1.8f));

        scene.Gi = Probes();
        var sealedOff = At(Converge(frame, scene), behind);
        scene.Gi = scene.Gi with { Visibility = false };
        var leaking = At(Converge(frame, scene), behind);

        // Blending by position alone hands the ground the bright probes' light through the wall.
        await Assert.That(leaking.R).IsGreaterThan(sealedOff.R + 25);
        await Assert.That(sealedOff.R).IsLessThan(20);
    }

    [Test]
    public async Task probes_follow_a_change_over_as_many_frames_as_their_hysteresis_keeps()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var frame = new WebGpuFrame(backend, renderer);
        var scene = DarkScene(GroundCamera());
        scene.Shapes.Add(SdfShape.Plane() with { Material = Matte(0.5f) });
        scene.Shapes.Add(Wall(Matte(1f)));
        var light = new SdfLight
        {
            Type = SdfLightType.Spot,
            Position = new Vector3(0f, 1f, 3f),
            Direction = Vector3.UnitZ,
            Intensity = 64f,
            InnerConeAngle = 10f * MathF.PI / 180f,
            OuterConeAngle = 14f * MathF.PI / 180f,
        };
        scene.Lights.Add(light);
        var near = GroundPixel(0f, 0f);

        scene.Gi = Probes();
        var lit = At(Converge(frame, scene), near);
        await Assert.That(lit.R).IsGreaterThan(40);

        // The light goes out. Three quarters of each probe's light survives a frame, so the
        // bounce fades instead of vanishing: 0.75 after one frame, under a hundredth after twenty.
        scene.Gi = scene.Gi with { Hysteresis = 0.75f };
        light.Intensity = 0f;
        var after = At(TestFrame.Render(backend, renderer, scene), near);
        await Assert.That(after.R).IsGreaterThan(lit.R * 3 / 4);
        await Assert.That(after.R).IsLessThan(lit.R);
        byte[] settled = [];
        for (var i = 0; i < 20; i++) settled = TestFrame.Render(backend, renderer, scene);
        await Assert.That(At(settled, near).R).IsLessThan(lit.R / 5);

        // With no hysteresis the next frame holds only what that frame traced.
        light.Intensity = 64f;
        scene.Gi = scene.Gi with { Hysteresis = 0f };
        Converge(frame, scene);
        light.Intensity = 0f;
        await Assert.That(At(TestFrame.Render(backend, renderer, scene), near)).IsEqualTo((0, 0, 0));
    }

    [Test]
    public async Task probes_keep_converging_while_nothing_draws_the_scene()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        var switches = new FeatureSwitches();
        using var renderer = new SdfRenderer(backend, switches, Size, Size);
        // The wall from probes_occlude_the_sky_and_return_what_the_ground_bounces: under the bare
        // sky it shows (142, 142, 142), and lit from converged probes (124, 124, 124).
        var scene = TestFrame.LinearScene(GroundCamera());
        var grey = new Vector3(0.5f);
        scene.Sky = new SdfSky { Zenith = grey, Horizon = grey, Ground = grey, AmbientIntensity = 1f };
        scene.Tracing = scene.Tracing with { AmbientOcclusion = 0f };
        scene.Shapes.Add(SdfShape.Plane() with { Material = Matte(0.5f) });
        scene.Shapes.Add(Wall(Matte(0.5f)));
        scene.Gi = Probes() with { Hysteresis = 0.8f };
        var wall = GroundPixel(0f, -0.75f, 1f);

        // With the scene switched off nothing reads the probes, and they still trace and blend.
        switches.Set(SdfFeatures.Scene.Id, false);
        for (var i = 0; i < 25; i++)
        {
            TestFrame.Render(backend, renderer, scene);
            await Assert.That(renderer.LastPassNames.SequenceEqual(["Sdf.ProbeTrace", "Sdf.ProbeBlend", "Sdf.Composite"])).IsTrue();
        }

        // So the first frame drawn is lit by converged probes. Culled, the probes would hold
        // nothing, and the wall would show the bare sky.
        switches.Set(SdfFeatures.Scene.Id, true);
        var first = At(TestFrame.Render(backend, renderer, scene), wall);
        await Assert.That(TestFrame.Near(first, TestFrame.Encode(new Vector3(0.54f * 0.5f * 0.75f)), 5)).IsTrue();
    }

    [Test]
    public async Task the_gi_switch_retracts_the_probe_passes_and_restores_the_sky()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        var switches = new FeatureSwitches();
        using var renderer = new SdfRenderer(backend, switches, Size, Size);
        var scene = TestFrame.LinearScene(GroundCamera());
        scene.Sky = scene.Sky with { AmbientIntensity = 1f };
        scene.Shapes.Add(SdfShape.Plane() with { Material = Matte(0.5f) });
        scene.Shapes.Add(Wall(Matte(0.5f)));
        var gi = renderer.Pipeline.Find<SdfGiFeature>()!;

        var plain = TestFrame.Render(backend, renderer, scene).ToArray();
        await Assert.That(renderer.LastPassNames.SequenceEqual(["Sdf.Scene", "Sdf.Composite"])).IsTrue();
        await Assert.That(gi.RaysTraced).IsEqualTo(0);

        scene.Gi = Probes() with { RaysPerProbe = 16 };
        var lit = TestFrame.Render(backend, renderer, scene).ToArray();
        await Assert.That(renderer.LastPassNames.SequenceEqual(["Sdf.ProbeTrace", "Sdf.ProbeBlend", "Sdf.Scene", "Sdf.Composite"])).IsTrue();
        await Assert.That(gi.Grid.Count).IsEqualTo(7 * 4 * 7);
        await Assert.That(gi.RaysTraced).IsEqualTo(16 * 7 * 4 * 7);
        await Assert.That(lit.AsSpan().SequenceEqual(plain)).IsFalse();

        // Probes that contribute nothing leave the sky's picture, drawn by the probe program.
        var feature = renderer.Pipeline.Find<SdfSceneFeature>()!;
        scene.Gi = scene.Gi with { Intensity = 0f };
        await Assert.That(LargestDifference(TestFrame.Render(backend, renderer, scene), plain)).IsLessThanOrEqualTo(2);
        await Assert.That(feature.UsedProbes).IsTrue();
        scene.Gi = scene.Gi with { Intensity = 1f };

        switches.Set(SdfFeatures.Gi.Id, false);
        var off = TestFrame.Render(backend, renderer, scene);
        await Assert.That(feature.UsedProbes).IsFalse();
        await Assert.That(off.AsSpan().SequenceEqual(plain)).IsTrue();
        await Assert.That(renderer.LastPassNames.SequenceEqual(["Sdf.Scene", "Sdf.Composite"])).IsTrue();
        await Assert.That(gi.RaysTraced).IsEqualTo(0);

        // Back on, the probes start from nothing: the first frame equals the first frame before.
        switches.Set(SdfFeatures.Gi.Id, true);
        scene.Gi = scene.Gi with { Hysteresis = 0.9f };
        await Assert.That(TestFrame.Render(backend, renderer, scene).AsSpan().SequenceEqual(lit)).IsFalse();
        await Assert.That(gi.RaysTraced).IsEqualTo(16 * 7 * 4 * 7);
    }

    [Test]
    public async Task bloom_spreads_light_past_a_bright_edge_and_leaves_a_dim_scene_alone()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = DarkScene(TestFrame.Perspective(new Vector3(0f, 0f, 5f), Vector3.Zero));
        scene.Shapes.Add(SdfShape.Sphere(Vector3.Zero, 1f) with { Material = TestFrame.Emissive(4f, 4f, 4f) });
        // The silhouette is 17 pixels out.
        const int Outside = Center + 24;

        var plain = TestFrame.Render(backend, renderer, scene).ToArray();
        await Assert.That(TestFrame.Pixel(plain, Size, Outside, Center)).IsEqualTo((0, 0, 0));
        await Assert.That(renderer.LastPassNames.SequenceEqual(["Sdf.Scene", "Sdf.Composite"])).IsTrue();

        scene.Bloom = new SdfBloom { Enabled = true, Intensity = 0.5f };
        var glowing = TestFrame.Render(backend, renderer, scene).ToArray();
        var halo = TestFrame.Pixel(glowing, Size, Outside, Center);
        await Assert.That(halo.R).IsGreaterThan(10);
        await Assert.That(halo.R).IsLessThan(250);
        await Assert.That(TestFrame.Pixel(glowing, Size, 1, 1).R).IsLessThan(halo.R);
        // A 48-pixel base halves three times before it is 8 pixels or fewer.
        await Assert.That(renderer.LastPassNames.SequenceEqual(
        [
            "Sdf.Scene", "Sdf.Bloom.Bright", "Sdf.Bloom.Down", "Sdf.Bloom.Down", "Sdf.Bloom.Down",
            "Sdf.Bloom.Up", "Sdf.Bloom.Up", "Sdf.Bloom.Up", "Sdf.Composite",
        ])).IsTrue();

        scene.Bloom = scene.Bloom with { Intensity = 1f };
        var stronger = TestFrame.Render(backend, renderer, scene);
        await Assert.That(TestFrame.Pixel(stronger, Size, Outside, Center).R).IsGreaterThan(halo.R + 5);

        // Nothing below the threshold's knee blooms.
        scene.Shapes[0] = scene.Shapes[0] with { Material = TestFrame.Emissive(0.4f, 0.4f, 0.4f) };
        var dim = TestFrame.Render(backend, renderer, scene).ToArray();
        scene.Bloom = scene.Bloom with { Enabled = false };
        await Assert.That(TestFrame.Render(backend, renderer, scene).AsSpan().SequenceEqual(dim)).IsTrue();
    }

    [Test]
    public async Task fxaa_softens_a_silhouette_and_keeps_flat_color()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        var switches = new FeatureSwitches();
        using var renderer = new SdfRenderer(backend, switches, Size, Size);
        var scene = DarkScene(TestFrame.Perspective(new Vector3(0f, 0f, 5f), Vector3.Zero));
        scene.Shapes.Add(SdfShape.Sphere(Vector3.Zero, 1f) with { Material = TestFrame.Emissive(1f, 1f, 1f) });

        // One ray a pixel: every pixel is the sphere or the sky, nothing between.
        var aliased = TestFrame.Render(backend, renderer, scene).ToArray();
        await Assert.That(Intermediate(aliased)).IsEqualTo(0);

        scene.Fxaa = new SdfFxaa { Enabled = true };
        var smoothed = TestFrame.Render(backend, renderer, scene).ToArray();
        await Assert.That(Intermediate(smoothed)).IsGreaterThan(30);
        await Assert.That(TestFrame.Pixel(smoothed, Size, Center, Center)).IsEqualTo((255, 255, 255));
        await Assert.That(TestFrame.Pixel(smoothed, Size, 2, 2)).IsEqualTo((0, 0, 0));
        await Assert.That(renderer.LastPassNames.SequenceEqual(["Sdf.Scene", "Sdf.Composite", "Sdf.Fxaa"])).IsTrue();

        // Switched off, the composite presents the frame itself again.
        switches.Set(SdfFeatures.Fxaa.Id, false);
        await Assert.That(TestFrame.Render(backend, renderer, scene).AsSpan().SequenceEqual(aliased)).IsTrue();
        await Assert.That(renderer.LastPassNames.SequenceEqual(["Sdf.Scene", "Sdf.Composite"])).IsTrue();

        static int Intermediate(byte[] pixels)
        {
            var count = 0;
            for (var i = 0; i < pixels.Length; i += 4)
                if (pixels[i + 2] is > 8 and < 247) count++;
            return count;
        }
    }
}
