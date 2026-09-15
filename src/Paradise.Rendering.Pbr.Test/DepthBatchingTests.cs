using System.Numerics;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class DepthBatchingTests
{
    private const uint Size = 128;

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
            Position = new Vector3(0, 3, 7),
            View = PbrMath.LookAt(new Vector3(0, 3, 7), Vector3.Zero, Vector3.UnitY),
            Projection = PbrMath.Perspective(1f, 1f, 0.1f, 30f),
        },
        Ambient = new PbrAmbient { Sky = new Vector3(0.4f), Flat = true },
        Ssao = new PbrSsao { Enabled = true },
        Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
    };

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task depth_and_shadows_batch_across_materials_with_individual_transforms_and_palettes(bool skinned)
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var scene = Scene();
        var green = pbr.Materials.AddDefaultMaterial(new Vector4(0.2f, 0.8f, 0.2f, 1));
        var red = pbr.Materials.AddDefaultMaterial(new Vector4(0.8f, 0.2f, 0.2f, 1));
        var (vertices, indices) = Procedural.UnitCube();
        PbrPrimitive primitive;
        if (skinned)
        {
            var joints = new float[vertices.Length / 12 * 8];
            for (var i = 0; i < joints.Length; i += 8) joints[i + 4] = 1;
            primitive = pbr.UploadSkinnedPrimitive(vertices, joints, indices, green);
            pbr.SetJointPalette(0, [Matrix4x4.CreateTranslation(0.15f, 0.1f, 0),
                Matrix4x4.CreateTranslation(-0.15f, -0.1f, 0)]);
        }
        else primitive = pbr.UploadPrimitive(vertices, indices, green);
        for (var i = 0; i < 6; i++)
            scene.Instances.Add(new PbrInstance
            {
                Mesh = new PbrMesh([primitive with { MaterialId = i % 2 == 0 ? green : red }]),
                Model = Matrix4x4.CreateScale(0.6f, 0.8f + i * 0.03f, 0.7f)
                    * Matrix4x4.CreateRotationY(i * 0.2f)
                    * Matrix4x4.CreateTranslation((i % 3 - 1) * 1.5f, 0, i / 3 * -1.5f),
                JointOffset = skinned ? i % 2 : -1,
            });
        scene.Lights.Add(new PbrLight { Direction = Vector3.Normalize(new Vector3(1, 2, 1)), CastsShadows = true });
        var shadows = pbr.Pipeline.Find<ShadowFeature>()!;
        shadows.CascadeCount = 1;
        shadows.CasterCullingEnabled = false;
        var prepass = pbr.Pipeline.Find<PrepassFeature>()!;
        for (var frame = 0; frame < 2; frame++)
        {
            switches.Set(PbrFeatures.Instancing.Id, true);
            pbr.RenderFrame(scene);
            var packed = backend.ReadbackColor(out _, out _).ToArray();
            await Assert.That(prepass.DrawCalls).IsEqualTo(1);
            await Assert.That(prepass.SavedDrawCalls).IsEqualTo(5);
            await Assert.That(shadows.DrawCalls).IsEqualTo(1);
            await Assert.That(shadows.SavedDrawCalls).IsEqualTo(5);

            switches.Set(PbrFeatures.Instancing.Id, false);
            pbr.RenderFrame(scene);
            await Assert.That(prepass.DrawCalls).IsEqualTo(6);
            await Assert.That(shadows.DrawCalls).IsEqualTo(6);
            await Assert.That(packed.SequenceEqual(backend.ReadbackColor(out _, out _))).IsTrue();
            scene.Instances[2].Model *= Matrix4x4.CreateTranslation(0.1f, 0.2f, 0);
            if (skinned) pbr.SetJointPalette(1, [Matrix4x4.CreateTranslation(-0.2f, 0.3f, 0)]);
        }
    }

    [Test]
    public async Task light_frustum_culling_keeps_off_camera_shadow_casters()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        scene.Ssao = new PbrSsao { Enabled = false };
        scene.Camera = new PbrCamera
        {
            Position = new Vector3(0, 10, 0),
            View = PbrMath.LookAt(new Vector3(0, 10, 0), Vector3.Zero, Vector3.UnitZ),
            Projection = PbrMath.Orthographic(8, 1, 0.1f, 30f),
        };
        var (vertices, indices) = Procedural.UnitCube();
        var material = pbr.Materials.AddDefaultMaterial(new Vector4(0.8f, 0.8f, 0.8f, 1));
        var mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, material)]);
        scene.Instances.Add(new PbrInstance { Mesh = mesh, Model = Matrix4x4.CreateScale(12, 0.1f, 12) });
        scene.Instances.Add(new PbrInstance { Mesh = mesh, Model = Matrix4x4.CreateTranslation(5, 5, 0) });
        scene.Instances.Add(new PbrInstance { Mesh = mesh, Model = Matrix4x4.CreateTranslation(100, 0, 100) });
        scene.Lights.Add(new PbrLight { Direction = Vector3.Normalize(new Vector3(1, 1, 0)), CastsShadows = true });
        var shadows = pbr.Pipeline.Find<ShadowFeature>()!;
        shadows.CascadeCount = 1;
        shadows.MaxDistance = 20;
        pbr.RenderFrame(scene);
        var culled = backend.ReadbackColor(out _, out _).ToArray();
        await Assert.That(pbr.Pipeline.Find<FrustumCullingFeature>()!.CulledDrawCount).IsEqualTo(2);
        await Assert.That(shadows.CulledDrawCount).IsGreaterThan(0);
        shadows.CasterCullingEnabled = false;
        pbr.RenderFrame(scene);
        await Assert.That(culled.SequenceEqual(backend.ReadbackColor(out _, out _))).IsTrue();
        scene.Instances.RemoveAt(1);
        pbr.RenderFrame(scene);
        var withoutCaster = backend.ReadbackColor(out _, out _);
        var shadowPixels = 0;
        for (var i = 0; i < culled.Length; i += 4)
            if (withoutCaster[i] > culled[i] + 10) shadowPixels++;
        await Assert.That(shadowPixels).IsGreaterThan(16);
    }

    [Test]
    public async Task light_frustum_culling_keeps_mutable_skinned_and_unknown_bounds()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene();
        var (vertices, indices) = Procedural.UnitCube();
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var primitive = pbr.UploadPrimitive(vertices, indices, material);
        var joints = new float[vertices.Length / 12 * 8];
        for (var i = 0; i < joints.Length; i += 8) joints[i + 4] = 1;
        var skinned = pbr.UploadSkinnedPrimitive(vertices, joints, indices, material);
        pbr.SetJointPalette(0, [Matrix4x4.Identity]);
        foreach (var descriptor in new[] { primitive, primitive with { Dynamic = true },
            primitive with { LocalMin = default, LocalMax = default }, skinned })
            scene.Instances.Add(new PbrInstance
            {
                Mesh = new PbrMesh([descriptor]), Model = Matrix4x4.CreateTranslation(100, 0, 0),
                JointOffset = descriptor.Skinned ? 0 : -1,
            });
        scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Spot, Position = new Vector3(0, 5, 0), Direction = Vector3.UnitY,
            Range = 10, SpotOuterDegrees = 60, CastsShadows = true,
        });
        var shadows = pbr.Pipeline.Find<ShadowFeature>()!;
        pbr.RenderFrame(scene);
        await Assert.That(shadows.CulledDrawCount).IsEqualTo(1);
        await Assert.That(shadows.DrawCalls + shadows.SavedDrawCalls).IsEqualTo(3);
    }
}
