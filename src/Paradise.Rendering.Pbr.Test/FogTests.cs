using System.Numerics;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class FogTests
{
    private const uint Size = 96;

    private static WebGpuRenderer? Backend()
    {
        try { return WebGpuRenderer.CreateHeadless(Size, Size); }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter: {error.Message}");
            return null;
        }
    }

    private static PbrScene Scene() => new()
    {
        Camera = new PbrCamera
        {
            View = PbrMath.LookAt(new Vector3(0, 0, 2), Vector3.Zero, Vector3.UnitY),
            Projection = PbrMath.Orthographic(4, 1, 0.1f, 20),
            Position = new Vector3(0, 0, 2),
        },
        ClearColor = new ColorRgba(1, 1, 1, 1),
        Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
        Fog = new PbrFog
        {
            Enabled = true, Density = 0.2f, HeightFalloff = 0, MaxDistance = 4,
            Color = Vector3.Zero, LightScattering = false,
        },
    };

    [Test]
    public async Task phase_function_integrates_to_unit_scattering()
    {
        foreach (var anisotropy in new[] { -0.7f, 0f, 0.7f })
        {
            double integral = 0;
            const int slices = 8192;
            for (var i = 0; i < slices; i++)
                integral += FogMath.HenyeyGreenstein(-1f + (i + 0.5f) * 2f / slices, anisotropy);
            integral *= 4 * Math.PI / slices;
            await Assert.That(integral).IsEqualTo(1.0).Within(0.0001);
        }
    }

    [Test]
    public async Task extinction_composes_across_independent_ray_segments()
    {
        await Assert.That(FogMath.Transmittance(0.3f, 3f) * FogMath.Transmittance(0.3f, 5f))
            .IsEqualTo(FogMath.Transmittance(0.3f, 8f)).Within(1e-6f);
        await Assert.That(FogMath.Transmittance(0, 100)).IsEqualTo(1f);
        await Assert.That(FogMath.Transmittance(100, 100)).IsEqualTo(0f);
    }

    [Test]
    public async Task gpu_homogeneous_fog_matches_beer_lambert_and_switches_retract()
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var scene = Scene();
        pbr.RenderFrame(scene);
        var fogged = backend.ReadbackColor(out _, out _).ToArray();
        var expected = ToSrgb(FogMath.Transmittance(0.2f, 4)) * 255;
        await Assert.That((float)fogged[((int)Size / 2 * (int)Size + (int)Size / 2) * 4]).IsEqualTo(expected).Within(2);
        await Assert.That(pbr.LastPassNames.Contains("Fog.Integrate")).IsTrue();
        switches.Set(PbrFeatures.Fog.Id, false);
        pbr.RenderFrame(scene);
        var off = backend.ReadbackColor(out _, out _).ToArray();
        await Assert.That(pbr.LastPassNames.Contains("Fog.Integrate")).IsFalse();
        await Assert.That(off[0]).IsEqualTo((byte)255);
        switches.Set(PbrFeatures.Fog.Id, true);
        scene.Fog = scene.Fog with { Density = 0 };
        pbr.RenderFrame(scene);
        await Assert.That(backend.ReadbackColor(out _, out _).AsSpan().SequenceEqual(off)).IsTrue();
        scene.Fog = scene.Fog with { Enabled = false, Density = 0.2f };
        pbr.RenderFrame(scene);
        await Assert.That(backend.ReadbackColor(out _, out _).AsSpan().SequenceEqual(off)).IsTrue();
    }

    [Test]
    public async Task local_volume_is_clipped_to_its_transformed_box()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        scene.Fog = scene.Fog with { Density = 0, MaxDistance = 6, Steps = 96 };
        scene.FogVolumes.Add(new PbrFogVolume
        {
            Density = 1,
            Transform = Matrix4x4.CreateScale(1, 4, 2) * Matrix4x4.CreateTranslation(-1, 0, -1),
        });
        pbr.RenderFrame(scene);
        var pixels = backend.ReadbackColor(out _, out _).ToArray();
        var left = pixels[((int)Size / 2 * (int)Size + (int)Size / 4) * 4];
        var right = pixels[((int)Size / 2 * (int)Size + (int)Size * 3 / 4) * 4];
        await Assert.That((int)right - left).IsGreaterThan(80);
        await Assert.That(pbr.Pipeline.Find<FogFeature>()!.VolumeCount).IsEqualTo(1);
        scene.FogVolumes.Clear();
        pbr.RenderFrame(scene);
        await Assert.That(backend.ReadbackColor(out _, out _)[((int)Size / 2 * (int)Size + (int)Size / 4) * 4])
            .IsEqualTo((byte)255);
    }

    [Test]
    public async Task directional_light_scattering_lights_an_empty_medium()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        scene.ClearColor = new ColorRgba(0, 0, 0, 1);
        scene.Fog = scene.Fog with { LightScattering = true, Albedo = Vector3.One, Anisotropy = 0 };
        scene.Lights.Add(new PbrLight { Type = PbrLightType.Directional, Direction = -Vector3.UnitZ, Color = Vector3.UnitX, Intensity = 4 });
        pbr.RenderFrame(scene);
        var lit = backend.ReadbackColor(out _, out _).ToArray();
        await Assert.That(lit.Max()).IsEqualTo((byte)255);
        await Assert.That(lit.Where((_, i) => i % 4 != 3).Max()).IsGreaterThan((byte)80);
        scene.Fog = scene.Fog with { LightScattering = false };
        pbr.RenderFrame(scene);
        var dark = backend.ReadbackColor(out _, out _).ToArray();
        await Assert.That(dark.Where((_, i) => i % 4 != 3).Max()).IsEqualTo((byte)0);
    }

    private static float ToSrgb(float value) => value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;

    [Test]
    public async Task thin_local_volume_extinction_is_preserved_between_ray_samples()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        scene.Fog = scene.Fog with { Density = 0, MaxDistance = 6, Steps = 1 };
        scene.FogVolumes.Add(new PbrFogVolume
        {
            Density = 100,
            Transform = Matrix4x4.CreateScale(1, 4, 0.01f) * Matrix4x4.CreateTranslation(-1, 0, -1),
        });
        pbr.RenderFrame(scene);
        var coarse = Pixels(backend, "thin-volume");
        await Assert.That((float)Pixel(coarse, Size / 4, Size / 2)).IsEqualTo(ToSrgb(MathF.Exp(-1)) * 255).Within(2);
        await Assert.That(Pixel(coarse, Size * 3 / 4, Size / 2)).IsEqualTo((byte)255);
        scene.Fog = scene.Fog with { Steps = 96 };
        pbr.RenderFrame(scene);
        await Assert.That((int)Pixel(Pixels(backend), Size / 4, Size / 2))
            .IsEqualTo(Pixel(coarse, Size / 4, Size / 2)).Within(2);
    }

    [Test]
    public async Task nonfinite_colors_are_sanitized_and_invalid_volume_transforms_are_rejected()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        scene.Fog = scene.Fog with { Color = new Vector3(float.NaN, 0.2f, float.PositiveInfinity) };
        pbr.RenderFrame(scene);
        var sanitized = Pixels(backend);
        scene.Fog = scene.Fog with { Color = new Vector3(0, 0.2f, 0) };
        pbr.RenderFrame(scene);
        await Assert.That(Pixels(backend).SequenceEqual(sanitized)).IsTrue();
        var nonfinite = Matrix4x4.Identity;
        nonfinite.M11 = float.NaN;
        var projective = Matrix4x4.Identity;
        projective.M14 = 0.1f;
        foreach (var transform in new[] { Matrix4x4.CreateScale(0f), nonfinite, projective })
        {
            scene.FogVolumes.Clear();
            scene.FogVolumes.Add(new PbrFogVolume { Transform = transform });
            await Assert.That(() => pbr.RenderFrame(scene)).Throws<ArgumentException>();
        }
        scene.FogVolumes.Clear();
        for (var i = 0; i <= FogFeature.MaxVolumes; i++) scene.FogVolumes.Add(new PbrFogVolume());
        await Assert.That(() => pbr.RenderFrame(scene)).Throws<ArgumentException>();
        scene.FogVolumes.Clear();
        pbr.RenderFrame(scene);
        await Assert.That(pbr.Pipeline.Find<FogFeature>()!.VolumeCount).IsEqualTo(0);
    }

    private static void AddWall(PbrRenderer pbr, PbrScene scene, Matrix4x4? transform = null)
    {
        float[] vertices = [-4, -4, 0, 0, 0, 1, 0, 0, 1, 0, 0, 1,
            4, -4, 0, 0, 0, 1, 1, 0, 1, 0, 0, 1,
            4, 4, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1,
            -4, 4, 0, 0, 0, 1, 0, 1, 1, 0, 0, 1];
        var material = pbr.Materials.AddDefaultMaterial(new Vector4(0.5f, 0.5f, 0.5f, 1), metallic: 0, roughness: 1);
        var primitive = pbr.UploadPrimitive(vertices, [0, 1, 2, 0, 2, 3], material);
        scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh([primitive]), Model = transform ?? Matrix4x4.Identity });
    }

    [Test]
    public async Task opaque_depth_limits_fog_for_both_camera_projections()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        scene.Ambient = new PbrAmbient { Flat = true, Sky = Vector3.One };
        AddWall(pbr, scene);
        foreach (var perspective in new[] { false, true })
        {
            scene.Camera = scene.Camera with { Projection = perspective
                ? PbrMath.Perspective(MathF.PI / 3, 1, 0.1f, 20) : PbrMath.Orthographic(4, 1, 0.1f, 20) };
            scene.Fog = scene.Fog with { Enabled = false };
            pbr.RenderFrame(scene);
            var baseline = ToLinear(Pixel(Pixels(backend), Size / 2, Size / 2));
            await Assert.That(baseline).IsGreaterThan(0.1f);
            foreach (var maximum in new[] { 1f, 10f })
            {
                scene.Fog = scene.Fog with { Enabled = true, Density = 0.4f, StartDistance = 0.5f, MaxDistance = maximum };
                pbr.RenderFrame(scene);
                var length = MathF.Min(maximum, perspective ? 2f : 1.9f) - 0.5f;
                await Assert.That(ToLinear(Pixel(Pixels(backend), Size / 2, Size / 2)))
                    .IsEqualTo(baseline * MathF.Exp(-0.4f * length)).Within(0.01f);
            }
        }
    }

    [Test]
    public async Task height_falloff_matches_density_at_each_orthographic_ray()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        scene.Fog = scene.Fog with { HeightFalloff = 1 };
        pbr.RenderFrame(scene);
        var pixels = Pixels(backend);
        foreach (var y in new[] { Size / 4, Size * 3 / 4 })
        {
            var worldY = (1 - 2 * (y + 0.5f) / Size) * 2;
            var expected = ToSrgb(MathF.Exp(-0.2f * MathF.Exp(-worldY) * 4)) * 255;
            await Assert.That((float)Pixel(pixels, Size / 2, y)).IsEqualTo(expected).Within(2);
        }
    }

    [Test]
    public async Task fog_chains_with_aa_and_bloom_and_survives_resize_and_switches()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        scene.Taa = new PbrTaa { Enabled = true };
        scene.Fxaa = new PbrFxaa { Enabled = true };
        scene.Bloom = new PbrBloom { Enabled = true, Threshold = 2 };
        foreach (var size in new[] { Size, 97u, Size })
        {
            backend.Resize(size, size);
            pbr.Resize(size, size);
            for (var i = 0; i < 3; i++) pbr.RenderFrame(scene);
            var pixels = backend.ReadbackColor(out var width, out var height);
            await Assert.That(width).IsEqualTo(size);
            await Assert.That(height).IsEqualTo(size);
            await Assert.That((float)Pixel(pixels, size / 2, size / 2, size))
                .IsEqualTo(ToSrgb(MathF.Exp(-0.8f)) * 255).Within(2);
            var passes = pbr.LastPassNames.ToList();
            await Assert.That(passes.IndexOf("Fog.Integrate")).IsGreaterThan(-1);
            await Assert.That(passes.IndexOf("Taa.Resolve")).IsGreaterThan(passes.IndexOf("Fog.Integrate"));
            await Assert.That(passes.FindIndex(name => name.StartsWith("Bloom.", StringComparison.Ordinal)))
                .IsGreaterThan(passes.IndexOf("Taa.Resolve"));
        }
        pbr.Switches.Set(PbrFeatures.Fog.Id, false);
        pbr.RenderFrame(scene);
        await Assert.That(pbr.LastPassNames.Contains("Fog.Integrate")).IsFalse();
        await Assert.That(Pixel(Pixels(backend), Size / 2, Size / 2)).IsEqualTo((byte)255);
        pbr.Switches.Set(PbrFeatures.Fog.Id, true);
        pbr.RenderFrame(scene);
        await Assert.That((float)Pixel(Pixels(backend), Size / 2, Size / 2))
            .IsEqualTo(ToSrgb(MathF.Exp(-0.8f)) * 255).Within(2);
    }

    [Test]
    public async Task shadow_maps_occlude_scattering_and_switching_restores_light()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        scene.ClearColor = new ColorRgba(0, 0, 0, 1);
        scene.Fog = scene.Fog with { Density = 0.5f, LightScattering = true, Albedo = Vector3.One, Anisotropy = 0 };

        AddWall(pbr, scene, Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(0.5f, 0, 0));
        var shadows = pbr.Pipeline.Find<ShadowFeature>()!;
        shadows.DirectionalRadius = 4;
        shadows.MapSize = 512;
        foreach (var type in new[] { PbrLightType.Directional, PbrLightType.Point, PbrLightType.Spot })
        {
            scene.Lights.Clear();
            scene.Lights.Add(new PbrLight { Type = type, Direction = Vector3.UnitX,
                Position = new Vector3(2, 0, 0), Range = 8, SpotOuterDegrees = 120,
                Intensity = 8, CastsShadows = true });
            pbr.RenderFrame(scene);
            var shadowed = Pixel(Pixels(backend, $"{type}-shadowed"), Size / 2, Size / 2);
            pbr.Switches.Set(PbrFeatures.Shadows.Id, false);
            pbr.RenderFrame(scene);
            var lit = Pixel(Pixels(backend, $"{type}-unshadowed"), Size / 2, Size / 2);
            await Assert.That((int)lit - shadowed).IsGreaterThan(50);
            pbr.Switches.Set(PbrFeatures.Shadows.Id, true);
            pbr.RenderFrame(scene);
            await Assert.That((int)Pixel(Pixels(backend), Size / 2, Size / 2)).IsEqualTo(shadowed).Within(2);
        }
    }

    [Test]
    public async Task coarse_shadow_maps_do_not_lift_volume_samples_through_a_blocker()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        scene.ClearColor = new ColorRgba(0, 0, 0, 1);
        scene.Fog = scene.Fog with { Density = 0.5f, LightScattering = true, Albedo = Vector3.One, Anisotropy = 0 };
        scene.Lights.Add(new PbrLight { Type = PbrLightType.Directional, Direction = Vector3.UnitX,
            Intensity = 4, CastsShadows = true });
        AddWall(pbr, scene, Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(0.045f, 0, 0));
        var shadows = pbr.Pipeline.Find<ShadowFeature>()!;
        shadows.DirectionalRadius = 4;
        shadows.MapSize = 256;
        pbr.RenderFrame(scene);
        var shadowed = Pixel(Pixels(backend), Size / 2, Size / 2);
        pbr.Switches.Set(PbrFeatures.Shadows.Id, false);
        pbr.RenderFrame(scene);
        await Assert.That((int)Pixel(Pixels(backend), Size / 2, Size / 2) - shadowed).IsGreaterThan(50);
    }

    [Test]
    public async Task point_range_and_spot_direction_bound_local_scattering()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        scene.ClearColor = new ColorRgba(0, 0, 0, 1);
        scene.Fog = scene.Fog with { LightScattering = true, Albedo = Vector3.One, Anisotropy = 0 };
        var light = new PbrLight { Type = PbrLightType.Point, Position = Vector3.UnitY, Range = 4, Intensity = 4 };
        foreach (var type in new[] { PbrLightType.Point, PbrLightType.Spot })
        {
            scene.Lights.Clear();
            scene.Lights.Add(light with { Type = type, Direction = Vector3.UnitY });
            pbr.RenderFrame(scene);
            await Assert.That(Pixel(Pixels(backend), Size / 2, Size / 2)).IsGreaterThan((byte)30);
            scene.Lights[0] = type == PbrLightType.Point
                ? scene.Lights[0] with { Range = 0.01f }
                : scene.Lights[0] with { Direction = -Vector3.UnitY };
            pbr.RenderFrame(scene);
            await Assert.That(Pixel(Pixels(backend), Size / 2, Size / 2)).IsEqualTo((byte)0);
        }
    }

    [Test]
    public async Task overlapping_rotated_volumes_add_extinction_without_step_dependence()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        scene.Fog = scene.Fog with { Density = 0, MaxDistance = 6, Steps = 1 };
        // Rotation around Z preserves the two-metre depth while exercising local-space clipping.
        scene.FogVolumes.Add(new PbrFogVolume { Density = 0.2f,
            Transform = Matrix4x4.CreateScale(2, 2, 2) * Matrix4x4.CreateRotationZ(0.7f) });
        scene.FogVolumes.Add(new PbrFogVolume { Density = 0.3f,
            Transform = Matrix4x4.CreateScale(2, 2, 1) * Matrix4x4.CreateTranslation(0, 0, -0.5f) });
        foreach (var steps in new[] { 1, 32, 128 })
        {
            scene.Fog = scene.Fog with { Steps = steps };
            pbr.RenderFrame(scene);
            await Assert.That((float)Pixel(Pixels(backend), Size / 2, Size / 2))
                .IsEqualTo(ToSrgb(MathF.Exp(-0.7f)) * 255).Within(2);
        }
        pbr.Switches.Set(PbrFeatures.Fog.Id, false);
        pbr.RenderFrame(scene);
        await Assert.That(pbr.Pipeline.Find<FogFeature>()!.VolumeCount).IsEqualTo(0);
        pbr.Switches.Set(PbrFeatures.Fog.Id, true);
        scene.Fog = scene.Fog with { Enabled = false };
        pbr.RenderFrame(scene);
        await Assert.That(pbr.Pipeline.Find<FogFeature>()!.VolumeCount).IsEqualTo(0);
    }

    [Test]
    public async Task infinite_far_projection_preserves_sky_and_surface_fog_distances()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        foreach (var geometry in new[] { false, true })
        {
            if (geometry)
            {
                scene.Ambient = new PbrAmbient { Flat = true, Sky = Vector3.One };
                AddWall(pbr, scene);
            }
            scene.Camera = scene.Camera with { Projection = PbrMath.Perspective(MathF.PI / 3, 1, 0.1f, 100) };
            pbr.RenderFrame(scene);
            var finite = Pixel(Pixels(backend), Size / 2, Size / 2);
            scene.Camera = scene.Camera with { Projection = PbrMath.Perspective(MathF.PI / 3, 1, 0.1f, float.PositiveInfinity) };
            pbr.RenderFrame(scene);
            await Assert.That((int)Pixel(Pixels(backend), Size / 2, Size / 2)).IsEqualTo(finite).Within(2);
        }
    }

    private static float ToLinear(byte value)
    {
        var x = value / 255f;
        return x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    }

    private static byte[] Pixels(WebGpuRenderer backend, string? artifact = null)
    {
        var pixels = backend.ReadbackColor(out var width, out var height).ToArray();
        if (artifact is not null && Environment.GetEnvironmentVariable("PARADISE_FOG_ARTIFACTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            using var output = File.Create(Path.Combine(directory, artifact + ".png"));
            PngWriter.Write(output, new ColorReadback(pixels, width, height), backend.ColorFormat);
        }
        return pixels;
    }
    private static byte Pixel(byte[] pixels, uint x, uint y, uint width = Size, int channel = 0) =>
        pixels[(int)((y * width + x) * 4) + channel];
}
