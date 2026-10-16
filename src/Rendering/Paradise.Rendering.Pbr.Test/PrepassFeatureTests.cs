using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr.Test;

public class PrepassFeatureTests
{
    [Test]
    [Arguments(false, PbrAlphaMode.Opaque)]
    [Arguments(false, PbrAlphaMode.Blend)]
    [Arguments(true, PbrAlphaMode.Opaque)]
    [Arguments(true, PbrAlphaMode.Blend)]
    public async Task ssao_intensity_requires_enabled_ssao_and_opaque_geometry(bool enabled, PbrAlphaMode alphaMode)
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var (vertices, indices) = Procedural.UnitCube();
        var opaqueMaterial = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var primitive = pbr.UploadPrimitive(vertices, indices, opaqueMaterial);
        var opaqueInstance = new PbrInstance { Mesh = new PbrMesh([primitive]) };
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(new Vector3(0, 1, 4), Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                Position = new Vector3(0, 1, 4),
            },
            Ssao = new PbrSsao { Enabled = true, Intensity = 3f },
        };
        scene.Instances.Add(opaqueInstance);
        var uniforms = pbr.Pipeline.Find<PrepassFeature>()!.SsaoUniformBuffer;

        // Seed a nonzero prior frame so disabling SSAO cannot pass by retaining stale uniforms.
        pbr.RenderFrame(scene);
        await Assert.That(MemoryMarshal.Read<SsaoUniformsGpu>(backend.BufferData[uniforms]).Params.X).IsEqualTo(3f);

        var material = pbr.Materials.AddMaterial(new PbrMaterialDesc
        {
            BaseColorFactor = new Vector4(1f, 1f, 1f, 0.2f),
            AlphaMode = alphaMode,
        });
        scene.Instances.Clear();
        scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh([primitive with { MaterialId = material }]) });
        scene.Ssao = new PbrSsao { Enabled = enabled, Intensity = 2f };
        pbr.RenderFrame(scene);
        var expectedIntensity = enabled && alphaMode == PbrAlphaMode.Opaque ? 2f : 0f;
        await Assert.That(MemoryMarshal.Read<SsaoUniformsGpu>(backend.BufferData[uniforms]).Params.X)
            .IsEqualTo(expectedIntensity);

        scene.Instances.Clear();
        scene.Instances.Add(opaqueInstance);
        scene.Ssao = new PbrSsao { Enabled = true, Intensity = 1.5f };
        pbr.RenderFrame(scene);
        await Assert.That(MemoryMarshal.Read<SsaoUniformsGpu>(backend.BufferData[uniforms]).Params.X).IsEqualTo(1.5f);
    }
}
