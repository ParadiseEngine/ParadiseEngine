using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Assets.Gltf;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>The material-program extension seam end to end: waterFixture.slang (compiled by THIS
/// project the way a game compiles an extension — engine sources via -I) registers against a live
/// PbrRenderer, draws through its own pipeline beside a stock material, and its vertex-stage
/// heightfield displacement provably moves geometry. Registration is where mismatches must
/// surface, so the throwing paths are pinned too.</summary>
public class ShaderExtensionTests
{
    private static WebGpuRenderer? TryCreateHeadlessOrSkip(uint width = 64, uint height = 64)
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(width, height);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter available on this host: {ex.Message}");
            return null;
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native library not loadable on this host: {ex.Message}");
            return null;
        }
    }

    private static ShaderProgramDesc LoadFixture() =>
        ShaderProgramLoader.Load(typeof(ShaderExtensionTests).Assembly, "Shaders.waterFixture");

    private static GltfMaterialData FactorMaterial(Vector4 baseColor) => new(
        Name: "extension-fixture",
        BaseColorFactor: baseColor,
        MetallicFactor: 0f,
        RoughnessFactor: 0.4f,
        EmissiveFactor: Vector3.Zero,
        NormalScale: 1f,
        OcclusionStrength: 1f,
        TransmissionFactor: 0f,
        AlphaMode: GltfAlphaMode.Opaque,
        AlphaCutoff: 0.5f,
        DoubleSided: false,
        BaseColorImage: -1,
        MetallicRoughnessImage: -1,
        NormalImage: -1,
        OcclusionImage: -1,
        EmissiveImage: -1,
        BaseColorUvTransform: GltfUvTransform.Identity);

    private static (TextureHandle Texture, TextureViewHandle View) CreateHeightfield(IRenderer renderer)
    {
        var desc = new TextureDesc(
            "FixtureHeightfield", 4, 4, 1, 1, 1, TextureDimension.D2,
            TextureFormat.Rgba16Float, TextureUsage.TextureBinding | TextureUsage.CopyDst);
        var texture = renderer.CreateTexture(in desc);
        var view = renderer.CreateTextureView(new TextureViewDesc(
            "FixtureHeightfieldView", texture, TextureViewDimension.D2, 0, 1));
        WriteHeights(renderer, texture, 0f);
        return (texture, view);
    }

    private static void WriteHeights(IRenderer renderer, TextureHandle texture, float height)
    {
        var texels = new Half[4 * 4 * 4];
        for (var i = 0; i < 16; i++)
        {
            texels[i * 4] = (Half)height; // r = height; gba unused by the fixture
            texels[i * 4 + 3] = (Half)1f;
        }
        renderer.WriteTexture(texture, 0, MemoryMarshal.AsBytes<Half>(texels), 4 * 8, 4, 4, 4);
    }

    [Test]
    public async Task extension_program_draws_beside_stock_and_displaces_by_its_heightfield()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            var programId = pbr.RegisterMaterialProgram(LoadFixture());
            await Assert.That(programId).IsEqualTo(1);
            await Assert.That(pbr.CustomProgramCountForTest).IsEqualTo(1);

            var (heightTexture, heightView) = CreateHeightfield(renderer);

            var material = FactorMaterial(new Vector4(0.8f, 0.2f, 0.2f, 1f));
            var materialId = pbr.Materials.AddMaterial(in material, [], programId,
                [BindGroupEntryDesc.ForTextureView(7, heightView)]);
            await Assert.That(pbr.Materials.GetProgramId(materialId)).IsEqualTo(programId);

            // A stock cube and an extension cube side by side in one frame: two opaque pipelines,
            // keyed by program.
            var (vertices, indices) = Procedural.UnitCube();
            var stockMaterialId = pbr.Materials.AddDefaultMaterial(new Vector4(0.2f, 0.7f, 0.3f, 1f));
            var stockMesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, stockMaterialId)]);
            var extensionMesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, materialId)]);

            var scene = new PbrScene
            {
                Camera = new PbrCamera
                {
                    View = PbrMath.LookAt(new Vector3(0f, 1.5f, 3f), Vector3.Zero, Vector3.UnitY),
                    Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                    Position = new Vector3(0f, 1.5f, 3f),
                },
            };
            scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Directional,
                Direction = Vector3.Normalize(new Vector3(0.4f, 1f, 0.5f)),
                Intensity = 1.2f,
            });
            scene.Instances.Add(new PbrInstance { Mesh = stockMesh, Model = Matrix4x4.CreateTranslation(-0.8f, 0f, 0f) });
            scene.Instances.Add(new PbrInstance { Mesh = extensionMesh, Model = Matrix4x4.CreateTranslation(0.8f, 0f, 0f) });

            byte[] Render(float height)
            {
                WriteHeights(renderer, heightTexture, height);
                for (var i = 0; i < 3; i++) pbr.RenderFrame(scene);
                return (byte[])renderer.ReadbackColor(out _, out _).Clone();
            }

            var flat = Render(0f);
            await Assert.That(pbr.PipelineVariantCountForTest).IsEqualTo(2); // (0, Opaque) + (1, Opaque)

            // The assertion that separates working displacement from a shader that compiles and
            // ignores its heightfield: raising every texel one unit must move the extension cube.
            var raised = Render(1f);
            await Assert.That(flat.AsSpan().SequenceEqual(raised)).IsFalse();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task incompatible_program_layout_throws_at_registration()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            var fixture = LoadFixture();

            // Corrupt one frame-group binding the way a diverged include would: same slot,
            // different resource type. Registration — not the first draw — must reject it.
            var groups = (BindGroupLayoutDesc[])fixture.Layout.Groups.Clone();
            for (var i = 0; i < groups.Length; i++)
            {
                if (groups[i].GroupIndex != 1) continue;
                var entries = (BindGroupLayoutEntryDesc[])groups[i].Entries.Clone();
                entries[0] = entries[0] with { Type = BindingResourceType.ReadonlyStorageBuffer };
                groups[i] = new BindGroupLayoutDesc(1, entries);
            }
            var corrupted = new ShaderProgramDesc(fixture.Modules, new PipelineLayoutDesc(groups, fixture.Layout.PushConstants), fixture.VertexBuffers)
            {
                UniformBlocks = fixture.UniformBlocks,
                VertexBuffersByEntryPoint = fixture.VertexBuffersByEntryPoint,
            };

            await Assert.That(() => pbr.RegisterMaterialProgram(corrupted)).Throws<InvalidOperationException>();
            await Assert.That(pbr.CustomProgramCountForTest).IsEqualTo(0);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task extra_bind_entries_are_validated_against_the_program_layout()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            var programId = pbr.RegisterMaterialProgram(LoadFixture());
            var material = FactorMaterial(Vector4.One);

            // Unknown program id.
            await Assert.That(() => pbr.Materials.AddMaterial(in material, [], programId + 1))
                .Throws<ArgumentException>();
            // Missing the heightfield entry the program declares.
            await Assert.That(() => pbr.Materials.AddMaterial(in material, [], programId))
                .Throws<ArgumentException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task custom_program_on_a_skinned_primitive_throws_at_draw()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            var programId = pbr.RegisterMaterialProgram(LoadFixture());
            var (_, heightView) = CreateHeightfield(renderer);
            var material = FactorMaterial(Vector4.One);
            var materialId = pbr.Materials.AddMaterial(in material, [], programId,
                [BindGroupEntryDesc.ForTextureView(7, heightView)]);

            var (vertices, indices) = Procedural.UnitCube();
            var vertexCount = vertices.Length / 12;
            var jointsWeights = new float[vertexCount * 8];
            for (var i = 0; i < vertexCount; i++) jointsWeights[i * 8 + 4] = 1f;
            var skinned = pbr.UploadSkinnedPrimitive(vertices, jointsWeights, indices, materialId);

            var scene = new PbrScene
            {
                Camera = new PbrCamera
                {
                    View = PbrMath.LookAt(new Vector3(0f, 1.5f, 3f), Vector3.Zero, Vector3.UnitY),
                    Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                    Position = new Vector3(0f, 1.5f, 3f),
                },
            };
            scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh([skinned]), JointOffset = 0 });
            pbr.SetJointPalette(0, new[] { Matrix4x4.Identity });

            await Assert.That(() => pbr.RenderFrame(scene)).Throws<InvalidOperationException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }
}
