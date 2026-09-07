using System.Numerics;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>The tracer, proven by a picture: ray-traced ambient occlusion darkens a floor where a
/// wall meets it and leaves an open floor alone. The two scenes together separate "the rays find
/// the wall" from "the pass darkens everything", which a single darker frame cannot.</summary>
public class RayTracedAoTests
{
    private static WebGpuRenderer? TryCreateHeadlessOrSkip(uint size)
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(size, size);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter available on this host: {error.Message}");
            return null;
        }
    }

    /// <summary>A floor lit by ambient alone (no lights, so the ambient term is the whole picture),
    /// optionally with a wall standing on it.</summary>
    private static PbrScene BuildScene(PbrRenderer pbr, bool wall, bool rtao)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var floorId = pbr.Materials.AddDefaultMaterial(new Vector4(0.8f, 0.8f, 0.8f, 1f));
        var floor = new PbrMesh([pbr.UploadPrimitive(vertices, indices, floorId)]);

        var eye = new Vector3(0f, 2.5f, 3.5f);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, new Vector3(0f, 0f, 0f), Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                Position = eye,
            },
            Ambient = new PbrAmbient { Sky = Vector3.One, Equator = Vector3.One, Ground = Vector3.One, Flat = true },
            Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
            RayTracedAo = new PbrRayTracedAo { Enabled = rtao, RaysPerPixel = 16, MaxDistance = 2f },
        };
        scene.Instances.Add(new PbrInstance
        {
            Mesh = floor,
            Model = Matrix4x4.CreateScale(new Vector3(6f, 0.1f, 6f)) * Matrix4x4.CreateTranslation(0f, -0.05f, 0f),
        });
        if (wall)
        {
            scene.Instances.Add(new PbrInstance
            {
                Mesh = floor,
                Model = Matrix4x4.CreateScale(new Vector3(6f, 2f, 0.2f)) * Matrix4x4.CreateTranslation(0f, 1f, -1f),
            });
        }
        return scene;
    }

    private static double Mean(byte[] pixels)
    {
        long sum = 0;
        var count = 0;
        // Alpha is constant; average the color channels only.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
            count += 3;
        }
        return sum / (double)count;
    }

    private static byte[] Render(WebGpuRenderer backend, PbrRenderer pbr, PbrScene scene)
    {
        for (var i = 0; i < 3; i++) pbr.RenderFrame(scene);
        return (byte[])backend.ReadbackColor(out _, out _).Clone();
    }

    [Test]
    public async Task ray_traced_ao_darkens_the_floor_at_the_wall_and_not_the_open_floor()
    {
        var backend = TryCreateHeadlessOrSkip(128);
        if (backend is null) return;
        using var _ = backend;

        double walledOff, walledOn, openOff, openOn;
        using (var pbr = new PbrRenderer(backend, new FeatureSwitches(), 128, 128))
        {
            walledOff = Mean(Render(backend, pbr, BuildScene(pbr, wall: true, rtao: false)));
            walledOn = Mean(Render(backend, pbr, BuildScene(pbr, wall: true, rtao: true)));
        }
        using (var pbr = new PbrRenderer(backend, new FeatureSwitches(), 128, 128))
        {
            openOff = Mean(Render(backend, pbr, BuildScene(pbr, wall: false, rtao: false)));
            openOn = Mean(Render(backend, pbr, BuildScene(pbr, wall: false, rtao: true)));
        }

        // The wall occludes the floor along its base and the floor occludes the wall's base: a
        // measurable share of the frame darkens.
        await Assert.That(walledOff).IsGreaterThan(40.0);
        await Assert.That(walledOff - walledOn).IsGreaterThan(3.0);
        // An open floor sees nothing above it: the rays all escape and the picture is unchanged
        // beyond the ray budget's noise.
        await Assert.That(Math.Abs(openOff - openOn)).IsLessThan(0.5);
    }

    [Test]
    public async Task the_trace_pass_is_declared_only_while_enabled()
    {
        var backend = TryCreateHeadlessOrSkip(32);
        if (backend is null) return;
        using var _ = backend;
        var recorder = new Baseline.RecordingRenderer(backend);
        using var pbr = new PbrRenderer(recorder, new FeatureSwitches(), 32, 32);

        // Counted by NAME, and by the DELTA a dispatch count moves — not by how many compute
        // passes the frame holds. Forward+ light culling bins in compute every frame, so a
        // whole-frame count measures whoever else happens to dispatch.
        var scene = BuildScene(pbr, wall: true, rtao: false);
        pbr.RenderFrame(scene);
        var passesOff = CountPasses(pbr, "Rtao.");
        var dispatchesOff = CountKind(recorder.Frames[^1].Commands, RenderCommandKind.Dispatch);

        scene.RayTracedAo = scene.RayTracedAo with { Enabled = true };
        pbr.RenderFrame(scene);
        var passesOn = CountPasses(pbr, "Rtao.");
        var dispatchesOn = CountKind(recorder.Frames[^1].Commands, RenderCommandKind.Dispatch);

        await Assert.That(passesOff).IsEqualTo(0);
        await Assert.That(passesOn).IsEqualTo(1);
        await Assert.That(dispatchesOn - dispatchesOff).IsEqualTo(1);
    }

    private static int CountPasses(PbrRenderer pbr, string prefix)
    {
        var count = 0;
        foreach (var name in pbr.LastPassNames)
            if (name.StartsWith(prefix, StringComparison.Ordinal)) count++;
        return count;
    }

    private static int CountKind(RenderCommand[] commands, RenderCommandKind kind)
    {
        var count = 0;
        foreach (var command in commands)
            if (command.Kind == kind) count++;
        return count;
    }
}
