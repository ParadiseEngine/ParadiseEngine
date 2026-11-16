using System.Numerics;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>A material's sidedness reaches rasterization (#355): single-sided materials cull back
/// faces in every pass that must agree with the scene about which surfaces are visible, and
/// double-sided ones (the default) render as before.</summary>
public class DoubleSidedMaterialTests
{
    private static PbrScene Scene(PbrRenderer pbr, int doubleSided, int singleSided, bool instancing = true, bool occlusion = false)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(new Vector3(0, 1, 6), Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                Position = new Vector3(0, 1, 6),
            },
            // SSAO needs the depth/normal prepass; motion vectors need their pass.
            Ssao = new PbrSsao { Enabled = true, Intensity = 1f },
            MotionVectors = new PbrMotionVectors { Enabled = true },
            // Instanced and per-draw paths pick their pipelines separately; both must honour the material.
            Instancing = new PbrInstancing { Enabled = instancing },
            // Occlusion culling draws its own occluder depth, which must not fill in culled back faces.
            // It also turns main draws indirect, which never instance, hence its own case.
            Visibility = new PbrVisibility { FrustumEnabled = true, OcclusionEnabled = occlusion },
        };
        scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Directional,
            Direction = Vector3.Normalize(new Vector3(0.4f, 1f, 0.5f)),
            Intensity = 3f,
            CastsShadows = true,
        });
        // Consecutive copies of each, so instancing forms real batches rather than drawing singletons directly.
        var doubleSidedMesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, doubleSided)]);
        var singleSidedMesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, singleSided)]);
        for (var i = 0; i < 3; i++)
            scene.Instances.Add(new PbrInstance { Mesh = doubleSidedMesh, Model = Matrix4x4.CreateTranslation(-1.5f, i - 1f, 0f) });
        for (var i = 0; i < 3; i++)
            scene.Instances.Add(new PbrInstance { Mesh = singleSidedMesh, Model = Matrix4x4.CreateTranslation(1.5f, i - 1f, 0f) });
        return scene;
    }

    /// <summary>The cull modes of the pipelines a frame set, among those built from any of <paramref name="shaders"/>.</summary>
    private static HashSet<CullMode> CullsUsedBy(ResourceTrackingRenderer backend, params string[] shaders)
    {
        var sources = shaders.SelectMany(shader => ShaderPrograms.Load(shader).Modules.Select(module => module.Wgsl)).ToHashSet();
        return backend.LastCommands
            .Where(command => command.Kind == RenderCommandKind.SetPipeline)
            .Select(command => command.SetPipeline.Pipeline)
            .Where(pipeline => backend.Pipelines[pipeline].Modules.Any(module => sources.Contains(module.Wgsl)))
            .Select(pipeline => backend.PipelineCull[pipeline])
            .ToHashSet();
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task single_sided_materials_cull_back_faces_wherever_the_scene_decides_visibility(bool instancing, bool occlusion)
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var doubleSided = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var singleSided = pbr.Materials.AddMaterial(new PbrMaterialDesc { DoubleSided = false });
        var scene = Scene(pbr, doubleSided, singleSided, instancing, occlusion);

        pbr.RenderFrame(scene);
        pbr.RenderFrame(scene);

        await Assert.That(pbr.Materials.IsDoubleSided(doubleSided)).IsTrue();
        await Assert.That(pbr.Materials.IsDoubleSided(singleSided)).IsFalse();
        await Assert.That(CullsUsedBy(backend, "Shaders.pbr", "Shaders.pbrInstanced")).IsEquivalentTo([CullMode.None, CullMode.Back]);
        await Assert.That(CullsUsedBy(backend, "Shaders.depthNormalPrepass", "Shaders.depthNormalPrepassInstanced"))
            .IsEquivalentTo([CullMode.None, CullMode.Back]);
        await Assert.That(CullsUsedBy(backend, "Shaders.motionVectors")).IsEquivalentTo([CullMode.None, CullMode.Back]);
        await Assert.That(CullsUsedBy(backend, "Shaders.occluderDepth"))
            .IsEquivalentTo(occlusion ? new[] { CullMode.None, CullMode.Back } : []);
        // Casters render both sides whatever the material says, so a wall still blocks light from behind.
        await Assert.That(CullsUsedBy(backend, "Shaders.shadow", "Shaders.shadowInstanced")).IsEquivalentTo([CullMode.None]);
    }

    /// <summary>Pass pipeline tables are sized from PipelineVariants; a new CullMode member must
    /// widen them rather than index past them.</summary>
    [Test]
    public async Task pipeline_variants_cover_every_cull_mode_once()
    {
        var modes = Enum.GetValues<CullMode>();
        var indices = new HashSet<int>();
        foreach (var skinned in new[] { false, true })
        {
            foreach (var cull in modes)
            {
                var index = PipelineVariants.Index(skinned, cull);
                await Assert.That(index).IsGreaterThanOrEqualTo(0).And.IsLessThan(PipelineVariants.Count);
                await Assert.That(PipelineVariants.IsSkinned(index)).IsEqualTo(skinned);
                await Assert.That(PipelineVariants.Cull(index)).IsEqualTo(cull);
                indices.Add(index);
            }
        }

        await Assert.That(modes.Length).IsEqualTo(PipelineVariants.CullModes);
        await Assert.That(indices.Count).IsEqualTo(PipelineVariants.Count);
    }

    [Test]
    public async Task materials_are_double_sided_unless_they_say_otherwise()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var first = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var second = pbr.Materials.AddDefaultMaterial(new Vector4(1f, 0f, 0f, 1f));

        pbr.RenderFrame(Scene(pbr, first, second));

        await Assert.That(new PbrMaterialDesc().DoubleSided).IsTrue();
        await Assert.That(CullsUsedBy(backend, "Shaders.pbr", "Shaders.pbrInstanced")).IsEquivalentTo([CullMode.None]);
        await Assert.That(CullsUsedBy(backend, "Shaders.depthNormalPrepass", "Shaders.depthNormalPrepassInstanced"))
            .IsEquivalentTo([CullMode.None]);
    }
}
