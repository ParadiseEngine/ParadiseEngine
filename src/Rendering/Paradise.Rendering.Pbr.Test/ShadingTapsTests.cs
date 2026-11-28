using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>The per-pixel tap counts a host can lower: SSAO's pre-pass taps and the soft-shadow disk.</summary>
public class ShadingTapsTests
{
    private static (PbrRenderer Pbr, PbrScene Scene) Setup(ResourceTrackingRenderer backend)
    {
        var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var (vertices, indices) = Procedural.UnitCube();
        var primitive = pbr.UploadPrimitive(vertices, indices, pbr.Materials.AddDefaultMaterial(Vector4.One));
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(new Vector3(0, 1, 4), Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                Position = new Vector3(0, 1, 4),
            },
            Ssao = new PbrSsao { Enabled = true },
        };
        scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh([primitive]) });
        scene.Lights.Add(new PbrLight { Direction = Vector3.Normalize(new Vector3(-1, 2, 1)), Intensity = 1, CastsShadows = true, SoftShadows = true });
        return (pbr, scene);
    }

    [Test]
    [Arguments(null, 12f)]
    [Arguments(6, 6f)]
    [Arguments(1, 4f)]
    [Arguments(100, 32f)]
    public async Task ssao_samples_reach_the_uniform_within_their_range(int? samples, float expected)
    {
        var backend = new ResourceTrackingRenderer();
        var (pbr, scene) = Setup(backend);
        using var _ = pbr;
        if (samples is { } count) scene.Ssao = scene.Ssao with { Samples = count };
        pbr.RenderFrame(scene);
        var uniforms = pbr.Pipeline.Find<PrepassFeature>()!.SsaoUniformBuffer;
        await Assert.That(MemoryMarshal.Read<SsaoUniformsGpu>(backend.BufferData[uniforms]).Rtao.W).IsEqualTo(expected);
    }

    [Test]
    [Arguments(16, 16)]
    [Arguments(8, 8)]
    [Arguments(4, 8)]
    [Arguments(11, 8)]
    [Arguments(12, 16)]
    [Arguments(64, 16)]
    public async Task soft_shadow_taps_round_to_eight_or_sixteen_and_reach_the_frame(int requested, int expected)
    {
        var backend = new ResourceTrackingRenderer();
        var (pbr, scene) = Setup(backend);
        using var _ = pbr;
        var shadows = pbr.Pipeline.Find<ShadowFeature>()!;
        await Assert.That(shadows.SoftShadowTaps).IsEqualTo(16);
        shadows.SoftShadowTaps = requested;
        await Assert.That(shadows.SoftShadowTaps).IsEqualTo(expected);
        pbr.RenderFrame(scene);
        var frame = backend.Buffers.Single(entry => entry.Value.Name == "PbrFrameUniforms").Key;
        await Assert.That(MemoryMarshal.Read<FrameUniformsGpu>(backend.BufferData[frame]).ShadowFilter.Z).IsEqualTo((float)expected);
    }
}
