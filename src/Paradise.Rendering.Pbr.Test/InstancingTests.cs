using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.Rendering.Pbr.Test.Baseline;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class InstancingTests
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

    private static PbrScene Scene(PbrRenderer pbr, bool skinned, bool transparent = false)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var material = transparent ? pbr.Materials.AddMaterial(new GltfMaterialData(
            "glass", new Vector4(0.6f, 0.8f, 0.4f, 0.4f), 0f, 0.8f, Vector3.Zero, 1f, 1f,
            0f, GltfAlphaMode.Blend, 0.5f, true, -1, -1, -1, -1, -1, GltfUvTransform.Identity), [])
            : pbr.Materials.AddDefaultMaterial(new Vector4(0.6f, 0.8f, 0.4f, 1f));
        PbrPrimitive primitive;
        if (skinned)
        {
            var joints = new float[vertices.Length / 12 * 8];
            for (var i = 0; i < vertices.Length / 12; i++) joints[i * 8 + 4] = 1f;
            primitive = pbr.UploadSkinnedPrimitive(vertices, joints, indices, material);
            pbr.SetJointPalette(0, [Matrix4x4.CreateTranslation(0f, 0.2f, 0f), Matrix4x4.CreateTranslation(0f, -0.2f, 0f)]);
        }
        else primitive = pbr.UploadPrimitive(vertices, indices, material);
        var mesh = new PbrMesh([primitive]);
        var eye = new Vector3(0f, 2f, 6f);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY), Position = eye,
                Projection = PbrMath.Perspective(1f, 1f, 0.1f, 30f),
            },
            ClearColor = new ColorRgba(0, 0, 0, 1),
            Ambient = new PbrAmbient { Sky = new Vector3(0.5f), Flat = true },
            Ssao = new PbrSsao { Enabled = !transparent },
        };
        scene.Lights.Add(new PbrLight { Type = PbrLightType.Directional, Direction = Vector3.UnitY, CastsShadows = true });
        for (var i = 0; i < 6; i++)
            scene.Instances.Add(new PbrInstance
            {
                Mesh = mesh,
                Model = Matrix4x4.CreateScale(0.7f, 0.9f, 0.5f) * Matrix4x4.CreateTranslation((i % 3 - 1) * 1.3f, 0f, i / 3 * -1.3f),
                Highlight = i * 0.15f,
                GiMode = i % 2 == 0 ? PbrGiMode.Disabled : PbrGiMode.Dynamic,
                JointOffset = skinned ? i % 2 : -1,
            });
        return scene;
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task batching_preserves_pixels_and_reduces_actual_draw_calls(bool skinned, bool transparent)
    {
        using var backend = Backend();
        if (backend is null) return;
        var recording = new RecordingRenderer(backend);
        var switches = new FeatureSwitches();
        using var pbr = new PbrRenderer(recording, switches, Size, Size);
        var scene = Scene(pbr, skinned, transparent);
        var feature = pbr.Pipeline.Find<InstancingFeature>()!;
        pbr.RenderFrame(scene);
        var batched = backend.ReadbackColor(out _, out _).ToArray();
        var calls = recording.LastPresentedFrame.Commands.Count(c => c.Kind == RenderCommandKind.DrawIndexed);
        await Assert.That(feature.DrawCalls).IsEqualTo(1);
        await Assert.That(feature.BatchedInstances).IsEqualTo(6);
        await Assert.That(feature.SavedDrawCalls).IsEqualTo(5);
        await Assert.That(recording.LastPresentedFrame.Commands.Any(c => c.Kind == RenderCommandKind.DrawIndexed && c.DrawIndexed.InstanceCount == 6)).IsTrue();

        switches.Set(PbrFeatures.Instancing.Id, false);
        pbr.RenderFrame(scene);
        var unbatched = backend.ReadbackColor(out _, out _).ToArray();
        var plainCalls = recording.LastPresentedFrame.Commands.Count(c => c.Kind == RenderCommandKind.DrawIndexed);
        await Assert.That(feature.DrawCalls).IsEqualTo(6);
        await Assert.That(plainCalls - calls).IsEqualTo(5);
        AssertPixels(batched, unbatched);
        await Assert.That(batched.Where((_, i) => i % 4 != 3).Count(v => v > 20)).IsGreaterThan(100);

        switches.Set(PbrFeatures.Instancing.Id, true);
        scene.Instances[2].Model *= Matrix4x4.CreateTranslation(0.2f, 0.3f, 0f);
        pbr.RenderFrame(scene);
        var moved = backend.ReadbackColor(out _, out _).ToArray();
        scene.Instancing = new PbrInstancing { Enabled = false };
        pbr.RenderFrame(scene);
        AssertPixels(moved, backend.ReadbackColor(out _, out _).ToArray());
        await Assert.That(feature.DrawCalls).IsEqualTo(6);
    }

    [Test]
    public async Task separate_material_and_geometry_runs_preserve_nonzero_first_instance()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, false);
        var (vertices, indices) = Procedural.UnitCube();
        var red = pbr.Materials.AddDefaultMaterial(new Vector4(0.9f, 0.1f, 0.1f, 1));
        scene.Instances.Insert(0, new PbrInstance
        {
            Mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, red)]),
            Model = Matrix4x4.CreateScale(0.3f) * Matrix4x4.CreateTranslation(0f, 1f, 0f),
        });
        pbr.RenderFrame(scene);
        var batched = backend.ReadbackColor(out _, out _).ToArray();
        await Assert.That(pbr.Pipeline.Find<InstancingFeature>()!.DrawCalls).IsEqualTo(2);
        scene.Instancing = new PbrInstancing { Enabled = false };
        pbr.RenderFrame(scene);
        AssertPixels(batched, backend.ReadbackColor(out _, out _).ToArray());
    }

    [Test]
    public async Task disabling_scene_clears_draw_statistics_when_instancing_is_already_off()
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        switches.Set(PbrFeatures.Instancing.Id, false);
        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var scene = Scene(pbr, false);
        var feature = pbr.Pipeline.Find<InstancingFeature>()!;

        pbr.RenderFrame(scene);
        await Assert.That(feature.DrawCalls).IsEqualTo(6);

        switches.Set(PbrFeatures.Scene.Id, false);
        pbr.RenderFrame(scene);
        await Assert.That(feature.DrawCalls).IsEqualTo(0);
        await Assert.That(feature.BatchedInstances).IsEqualTo(0);
        await Assert.That(feature.SavedDrawCalls).IsEqualTo(0);
    }

    private static void AssertPixels(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            var differences = expected.Zip(actual).Count(pair => pair.First != pair.Second);
            throw new InvalidOperationException($"Instanced image differs in {differences} bytes.");
        }
    }
}
