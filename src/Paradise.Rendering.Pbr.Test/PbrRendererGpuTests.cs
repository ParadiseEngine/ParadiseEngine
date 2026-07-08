using System.Numerics;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>Headless GPU coverage: the full PBR path (validated uniforms → material cache →
/// draw ring → opaque/blend buckets → submit) against a real adapter. Skip-not-fail without
/// one.</summary>
public class PbrRendererGpuTests
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

    private static PbrScene BuildCubeScene(PbrRenderer pbr, float transmission = 0f)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var materialId = pbr.Materials.AddDefaultMaterial(new Vector4(0.2f, 0.7f, 0.3f, 1f));
        var primitive = pbr.UploadPrimitive(vertices, indices, materialId);
        var mesh = new PbrMesh([primitive]);

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
        scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Point,
            Position = new Vector3(2f, 2f, 2f),
            Color = new Vector3(1f, 0.8f, 0.6f),
            Intensity = 6f,
            Range = 10f,
        });
        scene.Instances.Add(new PbrInstance { Mesh = mesh });
        _ = transmission;
        return scene;
    }

    [Test]
    public async Task procedural_cube_scene_renders_three_frames()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            var scene = BuildCubeScene(pbr);
            for (var i = 0; i < 3; i++)
            {
                scene.Instances[0].Model = Matrix4x4.CreateRotationY(i * 0.3f);
                pbr.RenderFrame(scene);
            }
            // Only the opaque pipeline variant was needed (lazy build).
            await Assert.That(pbr.PipelineVariantCountForTest).IsEqualTo(1);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task shadow_casting_lights_of_every_type_render_without_validation_errors()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            var (vertices, indices) = Procedural.UnitCube();
            var groundMat = pbr.Materials.AddDefaultMaterial(new Vector4(0.5f, 0.5f, 0.5f, 1f));
            var occluderMat = pbr.Materials.AddDefaultMaterial(new Vector4(0.8f, 0.3f, 0.2f, 1f));
            var ground = new PbrMesh([pbr.UploadPrimitive(vertices, indices, groundMat)]);
            var occluder = new PbrMesh([pbr.UploadPrimitive(vertices, indices, occluderMat)]);

            var scene = new PbrScene
            {
                Camera = new PbrCamera
                {
                    View = PbrMath.LookAt(new Vector3(4f, 5f, 6f), Vector3.Zero, Vector3.UnitY),
                    Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                    Position = new Vector3(4f, 5f, 6f),
                },
            };
            // Directional (1 tile) + spot (1 tile) + point (6 cube-face tiles) = 8 atlas tiles, all
            // casting shadows: exercises the tile allocator, spot/point light matrices, per-tile
            // viewport, and the point-light cube-face selection in the shader.
            scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Directional,
                Direction = Vector3.Normalize(new Vector3(0.4f, 1f, 0.3f)),
                Intensity = 1.2f,
                CastsShadows = true,
                ShadowStrength = 0.8f,
                SoftShadows = true,
            });
            scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Spot,
                Position = new Vector3(3f, 4f, 3f),
                Direction = Vector3.Normalize(new Vector3(-3f, -4f, -3f)), // surface→light ≈ toward the lamp
                Color = new Vector3(1f, 0.9f, 0.7f),
                Intensity = 8f,
                Range = 20f,
                SpotOuterDegrees = 50f,
                CastsShadows = true,
                ShadowStrength = 0.7f,
            });
            scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Point,
                Position = new Vector3(-2f, 3f, 2f),
                Color = new Vector3(0.7f, 0.8f, 1f),
                Intensity = 10f,
                Range = 15f,
                CastsShadows = true,
                ShadowStrength = 0.6f,
                SoftShadows = true,
            });
            scene.Instances.Add(new PbrInstance { Mesh = ground, Model = Matrix4x4.CreateScale(10f, 0.1f, 10f) });
            scene.Instances.Add(new PbrInstance { Mesh = occluder, Model = Matrix4x4.CreateTranslation(0f, 1.5f, 0f) });

            for (var i = 0; i < 3; i++) pbr.RenderFrame(scene);
            // No WebGPU validation error across the 8-tile atlas fill + comparison sampling is the tripwire.
            await Assert.That(pbr.PipelineVariantCountForTest).IsEqualTo(1);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task blend_material_builds_the_second_pipeline_variant()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            var scene = BuildCubeScene(pbr);

            // A transmissive material must route through the blend bucket.
            var (vertices, indices) = Procedural.UnitCube();
            var glassMaterial = new Paradise.Assets.Gltf.GltfMaterialData(
                Name: "glass",
                BaseColorFactor: new Vector4(0.9f, 0.95f, 1f, 0.4f),
                MetallicFactor: 0f,
                RoughnessFactor: 0.1f,
                EmissiveFactor: Vector3.Zero,
                NormalScale: 1f,
                OcclusionStrength: 1f,
                TransmissionFactor: 0.8f,
                AlphaMode: Paradise.Assets.Gltf.GltfAlphaMode.Blend,
                AlphaCutoff: 0.5f,
                DoubleSided: false,
                BaseColorImage: -1,
                MetallicRoughnessImage: -1,
                NormalImage: -1,
                OcclusionImage: -1,
                EmissiveImage: -1,
                BaseColorUvTransform: Paradise.Assets.Gltf.GltfUvTransform.Identity);
            var glassId = pbr.Materials.AddMaterial(in glassMaterial, []);
            await Assert.That(pbr.Materials.IsBlend(glassId)).IsTrue();

            var glassPrimitive = pbr.UploadPrimitive(vertices, indices, glassId);
            scene.Instances.Add(new PbrInstance
            {
                Mesh = new PbrMesh([glassPrimitive]),
                Model = Matrix4x4.CreateTranslation(0.4f, 0f, 0.8f),
            });

            pbr.RenderFrame(scene);
            await Assert.That(pbr.PipelineVariantCountForTest).IsEqualTo(2);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task headless_bgra8unorm_surface_selects_the_srgb_encoding_entry()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            // The headless target is Bgra8Unorm (non-sRGB) → the shader must encode.
            await Assert.That(renderer.ColorFormat).IsEqualTo(TextureFormat.Bgra8Unorm);
            await Assert.That(pbr.UsesSrgbEntryPointForTest).IsTrue();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task material_cache_dedupes_textures_by_image_and_usage()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            var fixturePath = System.IO.Path.Combine(FixtureRoot(), "color-srgb-etc1s.ktx2");
            var ktx2 = System.IO.File.ReadAllBytes(fixturePath);
            var images = new[] { new Paradise.Assets.Gltf.GltfImageData(ktx2) };

            Paradise.Assets.Gltf.GltfMaterialData Textured(string name) => new(
                Name: name,
                BaseColorFactor: Vector4.One,
                MetallicFactor: 1f,
                RoughnessFactor: 1f,
                EmissiveFactor: Vector3.Zero,
                NormalScale: 1f,
                OcclusionStrength: 1f,
                TransmissionFactor: 0f,
                AlphaMode: Paradise.Assets.Gltf.GltfAlphaMode.Opaque,
                AlphaCutoff: 0.5f,
                DoubleSided: false,
                BaseColorImage: 0,
                MetallicRoughnessImage: 0, // same image, LinearData usage → second texture
                NormalImage: -1,
                OcclusionImage: 0,         // LinearData again → cache hit
                EmissiveImage: 0,          // ColorSrgb again → cache hit
                BaseColorUvTransform: Paradise.Assets.Gltf.GltfUvTransform.Identity);

            try
            {
                var a = Textured("a");
                var b = Textured("b");
                _ = pbr.Materials.AddMaterial(in a, images);
                _ = pbr.Materials.AddMaterial(in b, images);
            }
            catch (DllNotFoundException ex)
            {
                Skip.Test($"libktx not loadable on this host: {ex.Message}");
                return;
            }

            // One image × two usages (ColorSrgb + LinearData) = exactly two GPU textures,
            // shared across both materials and all five slots.
            await Assert.That(pbr.Materials.TextureCount).IsEqualTo(2);
            await Assert.That(pbr.Materials.MaterialCount).IsEqualTo(2);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task texture_cache_keys_by_content_not_per_asset_image_index()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            // Two DIFFERENT images, each living at index 0 of its own asset's image array —
            // the cross-GLB layout that an index-keyed cache collides on.
            var colorBytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(FixtureRoot(), "color-srgb-etc1s.ktx2"));
            var normalBytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(FixtureRoot(), "normal-linear-uastc.ktx2"));
            var assetA = new[] { new Paradise.Assets.Gltf.GltfImageData(colorBytes) };
            var assetB = new[] { new Paradise.Assets.Gltf.GltfImageData(normalBytes) };
            // Byte-identical content in a THIRD asset (fresh array) must share A's texture.
            var assetC = new[] { new Paradise.Assets.Gltf.GltfImageData((byte[])colorBytes.Clone()) };

            Paradise.Assets.Gltf.GltfMaterialData BaseColorOnly(string name) => new(
                Name: name,
                BaseColorFactor: Vector4.One,
                MetallicFactor: 1f,
                RoughnessFactor: 1f,
                EmissiveFactor: Vector3.Zero,
                NormalScale: 1f,
                OcclusionStrength: 1f,
                TransmissionFactor: 0f,
                AlphaMode: Paradise.Assets.Gltf.GltfAlphaMode.Opaque,
                AlphaCutoff: 0.5f,
                DoubleSided: false,
                BaseColorImage: 0,
                MetallicRoughnessImage: -1,
                NormalImage: -1,
                OcclusionImage: -1,
                EmissiveImage: -1,
                BaseColorUvTransform: Paradise.Assets.Gltf.GltfUvTransform.Identity);

            try
            {
                var a = BaseColorOnly("a");
                var b = BaseColorOnly("b");
                var c = BaseColorOnly("c");
                _ = pbr.Materials.AddMaterial(in a, assetA);
                _ = pbr.Materials.AddMaterial(in b, assetB);
                _ = pbr.Materials.AddMaterial(in c, assetC);
            }
            catch (DllNotFoundException ex)
            {
                Skip.Test($"libktx not loadable on this host: {ex.Message}");
                return;
            }

            // Distinct contents → distinct textures (index keying collapsed these to 1);
            // identical contents across assets → shared texture (no third upload).
            await Assert.That(pbr.Materials.TextureCount).IsEqualTo(2);
            await Assert.That(pbr.Materials.MaterialCount).IsEqualTo(3);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    private static string FixtureRoot()
    {
        // The KTX2 fixtures live in the sibling Assets.Textures test project — reuse rather
        // than duplicate binaries in the repo.
        var dir = AppContext.BaseDirectory;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(
            dir, "..", "..", "..", "..", "Paradise.Assets.Textures.Test", "fixtures"));
    }
}
