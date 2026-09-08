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
}
