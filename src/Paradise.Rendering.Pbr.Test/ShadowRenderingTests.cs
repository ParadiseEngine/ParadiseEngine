using System.Numerics;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class ShadowRenderingTests
{
    private const uint Size = 192;

    private static WebGpuRenderer? Backend()
    {
        try { return WebGpuRenderer.CreateHeadless(Size, Size); }
        catch (Exception ex) when (ex is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter available: {ex.Message}");
            return null;
        }
    }

    private static PbrScene Scene(PbrRenderer pbr)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var material = pbr.Materials.AddDefaultMaterial(new Vector4(0.7f, 0.7f, 0.7f, 1), roughness: 1);
        var mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, material)]);
        var eye = new Vector3(3, 3, 5);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                Position = eye,
                View = PbrMath.LookAt(eye, new Vector3(0, 0.3f, 0), Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3, 1, 0.1f, 100),
            },
            Ambient = new PbrAmbient { Sky = new Vector3(0.03f), Equator = new Vector3(0.03f), Ground = new Vector3(0.03f), Flat = true },
            Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
        };
        scene.Instances.Add(new PbrInstance { Mesh = mesh, Model = Matrix4x4.CreateScale(10, 0.1f, 10) * Matrix4x4.CreateTranslation(0, -0.05f, 0) });
        scene.Instances.Add(new PbrInstance { Mesh = mesh, Model = Matrix4x4.CreateScale(0.8f, 1.6f, 0.8f) * Matrix4x4.CreateTranslation(0, 0.8f, 0) });
        scene.Lights.Add(new PbrLight
        {
            Direction = Vector3.Normalize(new Vector3(-0.7f, 1, 0.4f)),
            Intensity = 1,
            CastsShadows = true,
            SoftShadows = true,
        });
        return scene;
    }

    [Test]
    public async Task cascades_render_real_shadows_across_depth_slices_and_fit_one_atlas_pass()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var shadows = pbr.Pipeline.Find<ShadowFeature>()!;
        shadows.MaxDistance = 20;
        var scene = Scene(pbr);
        // Test the first, middle and far slices separately against their unshadowed images.
        foreach (var distance in new[] { 3f, 8f, 15f })
        {
            var eye = new Vector3(0, 3, distance);
            scene.Camera = scene.Camera with { Position = eye, View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY) };
            pbr.Switches.Set(PbrFeatures.Shadows.Id, true);
            var withShadows = Capture(pbr, backend, scene, $"cascades-{distance}");
            await Assert.That(shadows.Views.Count).IsEqualTo(4);
            await Assert.That(pbr.LastPassNames.Count(n => n.StartsWith("Shadow.", StringComparison.Ordinal))).IsEqualTo(1);
            pbr.Switches.Set(PbrFeatures.Shadows.Id, false);
            var without = Capture(pbr, backend, scene, $"cascades-{distance}-off");
            await Assert.That(DarkenedPixels(without, withShadows)).IsGreaterThan(8);
        }
    }

    [Test]
    public async Task atlas_packing_and_relocation_do_not_change_another_lights_pixels()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var shadows = pbr.Pipeline.Find<ShadowFeature>()!;
        shadows.AtlasSize = 2048;
        var scene = Scene(pbr);
        scene.Lights[0] = scene.Lights[0] with
        {
            Type = PbrLightType.Point, Position = new Vector3(2, 3, 2), Range = 20,
            Intensity = 10, ShadowResolution = 512,
        };
        var alone = Capture(pbr, backend, scene, "atlas-alone");
        var firstTile = shadows.Views[0].Tile;
        // A zero-energy light still draws into its tiles, so any sampling leakage is visible.
        scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Spot, Position = new Vector3(-2, 3, 1), Direction = Vector3.UnitY,
            Intensity = 0, CastsShadows = true, ShadowResolution = 1024, ShadowPriority = 10,
        });
        var packed = Capture(pbr, backend, scene, "atlas-packed");
        await Assert.That(shadows.Views[0].Tile == firstTile).IsFalse();
        await Assert.That(shadows.Views.Count).IsEqualTo(7);
        await Assert.That(alone.SequenceEqual(packed)).IsTrue();
        scene.Lights.RemoveAt(1);
        await Assert.That(alone.SequenceEqual(Capture(pbr, backend, scene, "atlas-reclaimed"))).IsTrue();
        shadows.AtlasSize = 1024;
        var smaller = Capture(pbr, backend, scene, "atlas-resized");
        await Assert.That(shadows.Views.Count).IsEqualTo(6);
        await Assert.That(shadows.Views.All(v => v.Tile.Size == 256)).IsTrue();
        scene.Lights[0] = scene.Lights[0] with { CastsShadows = false };
        var unshadowed = Capture(pbr, backend, scene, "atlas-off");
        await Assert.That(DarkenedPixels(unshadowed, alone)).IsGreaterThan(100);
        await Assert.That(DarkenedPixels(unshadowed, smaller)).IsGreaterThan(100);
    }

    [Test]
    public async Task pcss_emitter_size_changes_penumbrae_for_directional_and_local_lights()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var shadows = pbr.Pipeline.Find<ShadowFeature>()!;
        shadows.BlurTexels = 24;
        shadows.MaxDistance = 20;
        var scene = Scene(pbr);
        foreach (var type in new[] { PbrLightType.Directional, PbrLightType.Point, PbrLightType.Spot })
        {
            scene.Lights[0] = scene.Lights[0] with
            {
                Type = type, Position = new Vector3(-2, 3, 2), Direction = Vector3.Normalize(new Vector3(-2, 3, 2)),
                Intensity = type == PbrLightType.Directional ? 1 : 12, Range = 20, SpotOuterDegrees = 100,
                ShadowResolution = 1024, ShadowSourceRadius = 0, ShadowAngularDiameter = 0,
            };
            var pin = Capture(pbr, backend, scene, $"pcss-{type}-small");
            scene.Lights[0] = scene.Lights[0] with { ShadowSourceRadius = 1, ShadowAngularDiameter = 12 };
            var wide = Capture(pbr, backend, scene, $"pcss-{type}-wide");
            await Assert.That(ChangedPixels(pin, wide)).IsGreaterThan(25);
            scene.Lights[0] = scene.Lights[0] with { CastsShadows = false };
            var clear = Capture(pbr, backend, scene, $"pcss-{type}-off");
            await Assert.That(DarkenedPixels(clear, wide)).IsGreaterThan(50);
            scene.Lights[0] = scene.Lights[0] with { CastsShadows = true };
        }
    }

    [Test]
    public async Task pcss_penumbra_widens_when_a_blocker_moves_away_from_its_receiver()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var shadows = pbr.Pipeline.Find<ShadowFeature>()!;
        shadows.CascadeCount = 1;
        shadows.MaxDistance = 20;
        shadows.BlurTexels = 32;
        var scene = Scene(pbr);
        scene.Camera = new PbrCamera
        {
            Position = new Vector3(0, 10, 0),
            View = PbrMath.LookAt(new Vector3(0, 10, 0), Vector3.Zero, Vector3.UnitZ),
            Projection = PbrMath.Orthographic(8, 1, 0.1f, 20),
        };
        scene.Lights[0] = scene.Lights[0] with
        {
            Direction = Vector3.Normalize(new Vector3(1, 2, 0)), ShadowAngularDiameter = 20,
        };
        var partial = new List<int>();
        foreach (var height in new[] { 0.4f, 3f })
        {
            scene.Instances[1].Model = Matrix4x4.CreateScale(1, 0.05f, 1) * Matrix4x4.CreateTranslation(0, height, 0);
            scene.Lights[0] = scene.Lights[0] with { CastsShadows = true };
            var soft = Capture(pbr, backend, scene, $"pcss-separation-{height}");
            scene.Lights[0] = scene.Lights[0] with { CastsShadows = false };
            var clear = Capture(pbr, backend, scene, $"pcss-separation-{height}-off");
            // Count visibly partial coverage, excluding fully dark umbra and unchanged pixels.
            partial.Add(Enumerable.Range(0, clear.Length / 4).Count(pixel =>
            {
                var channel = pixel * 4;
                var ratio = (float)soft[channel] / Math.Max(clear[channel], (byte)1);
                return ratio > 0.45f && ratio < 0.9f;
            }));
        }
        await Assert.That(partial[1]).IsGreaterThan(partial[0] * 2 + 20);
    }

    [Test]
    public async Task a_single_cascade_stops_shadowing_beyond_its_maximum_camera_depth()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var shadows = pbr.Pipeline.Find<ShadowFeature>()!;
        shadows.CascadeCount = 1;
        shadows.MaxDistance = 2;
        var scene = Scene(pbr);
        scene.Camera = new PbrCamera
        {
            Position = new Vector3(0, 10, 0),
            View = PbrMath.LookAt(new Vector3(0, 10, 0), Vector3.Zero, Vector3.UnitZ),
            Projection = PbrMath.Orthographic(8, 1, 0.1f, 20),
        };
        scene.Lights[0] = scene.Lights[0] with { Direction = Vector3.Normalize(new Vector3(1, 2, 0)) };
        scene.Instances[1].Model = Matrix4x4.CreateTranslation(0, 3, 0);
        var beyond = Capture(pbr, backend, scene, "cascade-distance-clipped");
        scene.Lights[0] = scene.Lights[0] with { CastsShadows = false };
        var clear = Capture(pbr, backend, scene, "cascade-distance-off");
        await Assert.That(beyond.SequenceEqual(clear)).IsTrue();
        shadows.MaxDistance = 20;
        scene.Lights[0] = scene.Lights[0] with { CastsShadows = true };
        await Assert.That(DarkenedPixels(clear, Capture(pbr, backend, scene, "cascade-distance-visible"))).IsGreaterThan(50);
    }

    [Test]
    public async Task contact_shadows_darken_direct_light_and_retract_with_scene_feature_or_prepass_switches()
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        switches.Set(PbrFeatures.Shadows.Id, false);
        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var scene = Scene(pbr);
        var clear = Capture(pbr, backend, scene, "contact-off");
        scene.ContactShadows = new PbrContactShadows { Enabled = true, Length = 1, Thickness = 0.1f, Steps = 32 };
        var contact = Capture(pbr, backend, scene, "contact-on");
        await Assert.That(pbr.LastPassNames.Contains("Prepass.DepthNormal")).IsTrue();
        await Assert.That(DarkenedPixels(clear, contact)).IsGreaterThan(50);
        foreach (var feature in new[] { PbrFeatures.ContactShadows.Id, PbrFeatures.Prepass.Id })
        {
            switches.Set(feature, false);
            await Assert.That(clear.SequenceEqual(Capture(pbr, backend, scene, "contact-switch-off"))).IsTrue();
            switches.Set(feature, true);
            await Assert.That(contact.SequenceEqual(Capture(pbr, backend, scene, "contact-restored"))).IsTrue();
        }
        scene.ContactShadows = scene.ContactShadows with { Enabled = false };
        await Assert.That(clear.SequenceEqual(Capture(pbr, backend, scene, "contact-scene-off"))).IsTrue();
        scene.Lights[0] = scene.Lights[0] with { CastsShadows = false };
        scene.ContactShadows = scene.ContactShadows with { Enabled = true };
        await Assert.That(clear.SequenceEqual(Capture(pbr, backend, scene, "contact-noncasting"))).IsTrue();
    }

    private static byte[] Capture(PbrRenderer pbr, WebGpuRenderer backend, PbrScene scene, string name)
    {
        pbr.RenderFrame(scene);
        var pixels = (byte[])backend.ReadbackColor(out var width, out var height).Clone();
        var directory = Environment.GetEnvironmentVariable("PARADISE_SHADOW_ARTIFACTS");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            using var output = File.Create(Path.Combine(directory, name + ".png"));
            var capture = new ColorReadback(pixels, width, height);
            PngWriter.Write(output, in capture, backend.ColorFormat);
        }
        return pixels;
    }

    private static int DarkenedPixels(byte[] clear, byte[] shadow) =>
        Enumerable.Range(0, clear.Length / 4).Count(i =>
            clear[i * 4] + clear[i * 4 + 1] + clear[i * 4 + 2] > shadow[i * 4] + shadow[i * 4 + 1] + shadow[i * 4 + 2] + 12);

    private static int ChangedPixels(byte[] a, byte[] b) =>
        Enumerable.Range(0, a.Length / 4).Count(i => Math.Abs(a[i * 4] - b[i * 4]) +
            Math.Abs(a[i * 4 + 1] - b[i * 4 + 1]) + Math.Abs(a[i * 4 + 2] - b[i * 4 + 2]) > 12);
}
