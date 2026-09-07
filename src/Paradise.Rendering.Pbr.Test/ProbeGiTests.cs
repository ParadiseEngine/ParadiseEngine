using System.Numerics;
using Paradise.Rendering.Pbr.Test.Baseline;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>The probes, proven by pictures: light that can only arrive by bouncing appears when the
/// probes are on and not when they are off, and a scene lit by sky alone looks the same either way,
/// which pins the irradiance convention the probes and the sky ambient share.</summary>
public class ProbeGiTests
{
    private const uint Size = 96;

    private static WebGpuRenderer? TryCreateHeadlessOrSkip()
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(Size, Size);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter available on this host: {error.Message}");
            return null;
        }
    }

    private static PbrScene Camera(Vector3 eye, Vector3 target) => new()
    {
        Camera = new PbrCamera
        {
            View = PbrMath.LookAt(eye, target, Vector3.UnitY),
            Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
            Position = eye,
        },
        Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
    };

    /// <summary>Two facing walls in the dark: one glows red, the other is white and receives
    /// nothing but what bounces off the glow. The camera looks at the white one.</summary>
    private static PbrScene EmissiveRoom(PbrRenderer pbr, bool gi)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var white = pbr.Materials.AddDefaultMaterial(new Vector4(0.9f, 0.9f, 0.9f, 1f));
        var glow = pbr.Materials.AddMaterial(new Paradise.Assets.Gltf.GltfMaterialData(
            Name: "glow", BaseColorFactor: new Vector4(0.1f, 0.1f, 0.1f, 1f), MetallicFactor: 0f, RoughnessFactor: 1f,
            EmissiveFactor: new Vector3(6f, 0f, 0f), NormalScale: 1f, OcclusionStrength: 1f, TransmissionFactor: 0f,
            AlphaMode: Paradise.Assets.Gltf.GltfAlphaMode.Opaque, AlphaCutoff: 0.5f, DoubleSided: false,
            BaseColorImage: -1, MetallicRoughnessImage: -1, NormalImage: -1, OcclusionImage: -1, EmissiveImage: -1,
            BaseColorUvTransform: Paradise.Assets.Gltf.GltfUvTransform.Identity), []);
        var whiteMesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, white)]);
        var glowMesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, glow)]);

        // Camera between the walls, looking at the white one; the glowing wall is behind it.
        var scene = Camera(new Vector3(0f, 1f, 0.5f), new Vector3(0f, 1f, -2f));
        scene.Ambient = new PbrAmbient { Sky = Vector3.Zero, Equator = Vector3.Zero, Ground = Vector3.Zero, Flat = true };
        scene.Gi = new PbrGi { Enabled = gi, RaysPerProbe = 64, Hysteresis = 0.5f, MaxProbes = 512 };
        scene.Instances.Add(new PbrInstance { Mesh = whiteMesh, Model = Matrix4x4.CreateScale(new Vector3(6f, 4f, 0.2f)) * Matrix4x4.CreateTranslation(0f, 1f, -2f) });
        scene.Instances.Add(new PbrInstance { Mesh = glowMesh, Model = Matrix4x4.CreateScale(new Vector3(6f, 4f, 0.2f)) * Matrix4x4.CreateTranslation(0f, 1f, 2f) });
        scene.Instances.Add(new PbrInstance { Mesh = whiteMesh, Model = Matrix4x4.CreateScale(new Vector3(6f, 0.2f, 6f)) * Matrix4x4.CreateTranslation(0f, -1f, 0f) });
        return scene;
    }

    private static PbrScene OpenFloor(PbrRenderer pbr, bool gi)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var white = pbr.Materials.AddDefaultMaterial(new Vector4(0.6f, 0.6f, 0.6f, 1f));
        var scene = Camera(new Vector3(0f, 2f, 3f), Vector3.Zero);
        scene.Ambient = new PbrAmbient { Sky = new Vector3(0.5f), Equator = new Vector3(0.5f), Ground = new Vector3(0.5f), Flat = true };
        scene.Gi = new PbrGi { Enabled = gi, RaysPerProbe = 128, Hysteresis = 0.5f, MaxProbes = 256 };
        scene.Instances.Add(new PbrInstance
        {
            Mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, white)]),
            Model = Matrix4x4.CreateScale(new Vector3(6f, 0.1f, 6f)) * Matrix4x4.CreateTranslation(0f, -0.05f, 0f),
        });
        return scene;
    }

    private static (double R, double G, double B) Mean(byte[] pixels)
    {
        // BGRA8 readback.
        double r = 0, g = 0, b = 0;
        var count = pixels.Length / 4;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            b += pixels[i];
            g += pixels[i + 1];
            r += pixels[i + 2];
        }
        return (r / count, g / count, b / count);
    }

    private static byte[] Render(WebGpuRenderer backend, PbrRenderer pbr, PbrScene scene, int frames)
    {
        for (var i = 0; i < frames; i++) pbr.RenderFrame(scene);
        return (byte[])backend.ReadbackColor(out _, out _).Clone();
    }

    [Test]
    public async Task an_emissive_wall_lights_the_wall_facing_it_only_through_the_probes()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        (double R, double G, double B) off, on;
        using (var pbr = new PbrRenderer(backend, Size, Size))
            off = Mean(Render(backend, pbr, EmissiveRoom(pbr, gi: false), frames: 3));
        using (var pbr = new PbrRenderer(backend, Size, Size))
            on = Mean(Render(backend, pbr, EmissiveRoom(pbr, gi: true), frames: 12));

        // Without probes the white wall is unlit: black. With them it carries the red bounce, and
        // nothing else — the glow has no green or blue to bounce.
        await Assert.That(off.R).IsLessThan(2.0);
        await Assert.That(on.R).IsGreaterThan(12.0);
        await Assert.That(on.G).IsLessThan(on.R * 0.25);
        await Assert.That(on.B).IsLessThan(on.R * 0.25);
    }

    [Test]
    public async Task an_open_floor_under_a_flat_sky_looks_the_same_with_and_without_probes()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        (double R, double G, double B) off, on;
        using (var pbr = new PbrRenderer(backend, Size, Size))
            off = Mean(Render(backend, pbr, OpenFloor(pbr, gi: false), frames: 3));
        using (var pbr = new PbrRenderer(backend, Size, Size))
            on = Mean(Render(backend, pbr, OpenFloor(pbr, gi: true), frames: 12));

        // The probes see the same flat sky the ambient path uses, so the floor's brightness is the
        // same convention either way: a mismatch here is a missing π or a doubled exposure.
        // Some darkening is expected below the horizon: probes near the floor see the floor's own
        // albedo instead of sky for their downward rays, which the sky-only ambient cannot.
        await Assert.That(off.R).IsGreaterThan(40.0);
        await Assert.That(Math.Abs(on.R - off.R)).IsLessThan(off.R * 0.2);
    }

    /// <summary>The budget: tracing a handful of probes per frame reaches the same picture as
    /// tracing them all, given the frames to go round. What the window skips must be carried
    /// forward unchanged, or the untraced probes read as black holes in the atlas.</summary>
    [Test]
    public async Task a_probe_budget_smaller_than_the_volume_still_converges()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        (double R, double G, double B) all, budgeted;
        using (var pbr = new PbrRenderer(backend, Size, Size))
            all = Mean(Render(backend, pbr, EmissiveRoom(pbr, gi: true), frames: 12));
        using (var pbr = new PbrRenderer(backend, Size, Size))
        {
            var scene = EmissiveRoom(pbr, gi: true);
            scene.Gi = scene.Gi with { ProbesPerFrame = 16, Hysteresis = 0.3f };
            budgeted = Mean(Render(backend, pbr, scene, frames: 80));
        }

        await Assert.That(budgeted.R).IsGreaterThan(all.R * 0.6);
        await Assert.That(budgeted.R).IsLessThan(all.R * 1.4);
    }

    [Test]
    public async Task the_probe_passes_run_only_while_enabled_and_the_volume_fits_the_scene()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;
        var recorder = new RecordingRenderer(backend);
        using var pbr = new PbrRenderer(recorder, Size, Size);
        var gi = pbr.Pipeline.Find<ProbeGiFeature>()!;

        var scene = OpenFloor(pbr, gi: false);
        pbr.RenderFrame(scene);
        var computeOff = Count(recorder.Frames[^1].Commands, RenderCommandKind.BeginComputePass);
        var volumeOff = gi.ActiveVolume;

        scene.Gi = scene.Gi with { Enabled = true };
        pbr.RenderFrame(scene);
        var computeOn = Count(recorder.Frames[^1].Commands, RenderCommandKind.BeginComputePass);
        var dispatches = Count(recorder.Frames[^1].Commands, RenderCommandKind.Dispatch);
        var volume = gi.ActiveVolume;

        await Assert.That(computeOff).IsEqualTo(0);
        await Assert.That(volumeOff).IsNull();
        await Assert.That(computeOn).IsEqualTo(3);
        await Assert.That(dispatches).IsEqualTo(4);
        await Assert.That(volume).IsNotNull();
        // The floor is 6×0.1×6 around the origin plus the default half-metre margin: the fitted
        // grid spans it and stays under the probe budget.
        await Assert.That(volume!.CountX * volume.CountY * volume.CountZ).IsLessThanOrEqualTo(256);
        await Assert.That(volume.Origin.X).IsLessThanOrEqualTo(-3f);
        await Assert.That(volume.Origin.X + (volume.CountX - 1) * volume.Spacing.X).IsGreaterThanOrEqualTo(3f);
        await Assert.That(gi.ProbeCount).IsEqualTo(volume.CountX * volume.CountY * volume.CountZ);
    }

    [Test]
    public async Task a_fit_respects_the_probe_budget_and_the_atlas_limit()
    {
        var volume = ProbeGiFeature.Fit(new Geometry.Aabb(new Vector3(-100f, 0f, -100f), new Vector3(100f, 30f, 100f)), new PbrGi { MaxProbes = 4096 });
        await Assert.That(volume).IsNotNull();
        await Assert.That(volume!.CountX * volume.CountY * volume.CountZ).IsLessThanOrEqualTo(4096);
        await Assert.That(volume.CountX * volume.CountY * 16).IsLessThanOrEqualTo(8192);
        await Assert.That(volume.CountX).IsGreaterThanOrEqualTo(2);
        await Assert.That(ProbeGiFeature.Fit(Geometry.Aabb.Empty, new PbrGi())).IsNull();
    }

    private static int Count(RenderCommand[] commands, RenderCommandKind kind)
    {
        var count = 0;
        foreach (var command in commands)
            if (command.Kind == kind) count++;
        return count;
    }
}
