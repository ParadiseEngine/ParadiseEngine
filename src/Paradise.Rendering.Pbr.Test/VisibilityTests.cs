using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Assets.Gltf;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class VisibilityTests
{
    private const uint Size = 128;

    [Test]
    public async Task clip_planes_reject_outside_boxes_and_keep_intersections()
    {
        var projection = PbrMath.Perspective(MathF.PI / 2f, 1f, 0.1f, 10f);
        var extents = new Vector3(0.1f);
        foreach (var center in new[] { new Vector3(4, 0, -2), new Vector3(-4, 0, -2), new Vector3(0, 4, -2),
            new Vector3(0, -4, -2), new Vector3(0, 0, 2), new Vector3(0, 0, -11) })
            await Assert.That(Visibility.IntersectsFrustum(center - extents, center + extents, projection)).IsFalse();
        await Assert.That(Visibility.IntersectsFrustum(new Vector3(-1, -1, -2), new Vector3(1, 1, -1), projection)).IsTrue();
        await Assert.That(Visibility.IntersectsFrustum(new Vector3(-1), new Vector3(1), projection)).IsTrue();
        await Assert.That(Visibility.TryProject(new Vector3(-1), new Vector3(1), projection, Size, Size, out _, out _)).IsFalse();
    }

    [Test]
    public async Task transformed_boxes_containing_a_visible_point_are_never_rejected()
    {
        var random = new Random(92);
        var projection = PbrMath.Perspective(MathF.PI / 2f, 1f, 0.1f, 50f);
        var witnessed = 0;
        for (var i = 0; i < 2000; i++)
        {
            var min = new Vector3(-0.5f);
            var max = new Vector3(0.5f);
            var transform = Matrix4x4.CreateScale(0.1f + (float)random.NextDouble() * 4f)
                * Matrix4x4.CreateRotationY((float)random.NextDouble() * MathF.Tau)
                * Matrix4x4.CreateTranslation((float)random.NextDouble() * 20f - 10f, (float)random.NextDouble() * 8f - 4f,
                    -(float)random.NextDouble() * 20f) * projection;
            for (var j = 0; j < 8; j++)
            {
                var point = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
                var p = Vector4.Transform(new Vector4(point, 1f), transform);
                if (!(p.W > 0f && MathF.Abs(p.X) <= p.W && MathF.Abs(p.Y) <= p.W && p.Z >= 0f && p.Z <= p.W)) continue;
                witnessed++;
                if (!Visibility.IntersectsFrustum(min, max, transform)) throw new InvalidOperationException("Visible witness was culled.");
            }
        }
        await Assert.That(witnessed).IsGreaterThan(1000);
    }

    [Test]
    public async Task max_depth_reduction_preserves_holes_and_partial_edge_tiles()
    {
        var depth = Enumerable.Repeat(0.3f, 17 * 11).ToArray();
        depth[8 * 17 + 16] = 1f;
        var tiles = new float[6];
        Visibility.ReduceDepth(depth, 17, 11, tiles);
        await Assert.That(tiles[5]).IsEqualTo(1f);
        await Assert.That(Visibility.IsOccluded(new Vector4(0, 0, 7, 7), 0.4f, tiles, 3)).IsTrue();
        await Assert.That(Visibility.IsOccluded(new Vector4(16, 8, 16, 10), 0.4f, tiles, 3)).IsFalse();
        await Assert.That(Visibility.IsOccluded(new Vector4(0, 0, 7, 7), 0.3f, tiles, 3)).IsFalse();
    }

    private static WebGpuRenderer? Backend()
    {
        try { return WebGpuRenderer.CreateHeadless(Size, Size); }
        catch (Exception e) when (e is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter: {e.Message}");
            return null;
        }
    }

    private static PbrScene Scene(PbrRenderer renderer, bool mask = false, bool dynamic = false)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var material = new GltfMaterialData(
            Name: "occluder", BaseColorFactor: new Vector4(0.5f, 0.5f, 0.5f, mask ? 0f : 1f),
            MetallicFactor: 0f, RoughnessFactor: 1f, EmissiveFactor: new Vector3(0.3f), NormalScale: 1f,
            OcclusionStrength: 1f, TransmissionFactor: 0f, AlphaMode: mask ? GltfAlphaMode.Mask : GltfAlphaMode.Opaque,
            AlphaCutoff: 0.5f, DoubleSided: false, BaseColorImage: -1, MetallicRoughnessImage: -1,
            NormalImage: -1, OcclusionImage: -1, EmissiveImage: -1, BaseColorUvTransform: GltfUvTransform.Identity);
        var occluder = new PbrMesh([renderer.UploadPrimitive(vertices, indices, renderer.Materials.AddMaterial(material, []))]);
        var target = new PbrMesh([renderer.UploadPrimitive(vertices, indices,
            renderer.Materials.AddMaterial(material with { BaseColorFactor = Vector4.One, EmissiveFactor = new Vector3(1, 0, 0), AlphaMode = GltfAlphaMode.Opaque }, []), dynamic)]);
        var scene = new PbrScene
        {
            Camera = new PbrCamera { View = Matrix4x4.Identity, Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f) },
            Visibility = new PbrVisibility { FrustumEnabled = true, OcclusionEnabled = true },
            Bloom = new PbrBloom { Enabled = false },
            Gi = new PbrGi { Enabled = false },
            Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
            ClearColor = new ColorRgba(0, 0, 0, 1),
        };
        scene.Instances.Add(new PbrInstance { Mesh = occluder, Model = Matrix4x4.CreateScale(2f, 2f, 0.25f) * Matrix4x4.CreateTranslation(0, 0, -3) });
        scene.Instances.Add(new PbrInstance { Mesh = target, Model = Matrix4x4.CreateScale(0.4f) * Matrix4x4.CreateTranslation(0, 0, -5) });
        scene.Instances.Add(new PbrInstance { Mesh = target, Model = Matrix4x4.CreateScale(0.3f) * Matrix4x4.CreateTranslation(1.9f, 0, -4) });
        scene.Instances.Add(new PbrInstance { Mesh = target, Model = Matrix4x4.CreateTranslation(100, 0, -5) });
        return scene;
    }

    private static uint[] Arguments(WebGpuRenderer backend, OcclusionCullingFeature culling) =>
        MemoryMarshal.Cast<byte, uint>(backend.ReadbackBuffer(culling.IndirectBuffer, 0, (ulong)culling.DrawCount * OcclusionCullingFeature.IndirectStride)).ToArray();

    private static void SamePixels(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException("Culling changed the rendered image.");
    }

    [Test]
    public async Task gpu_culls_hidden_draws_matches_cpu_reference_and_preserves_pixels()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var culling = pbr.Pipeline.Find<OcclusionCullingFeature>()!;
        pbr.RenderFrame(scene);
        var pixels = backend.ReadbackColor(out _, out _).ToArray();
        var args = Arguments(backend, culling);
        var bounds = culling.Bounds.ToArray();
        var tiles = MemoryMarshal.Cast<byte, float>(backend.ReadbackBuffer(culling.DepthTiles, 0, culling.DepthTileBytes)).ToArray();
        await Assert.That(args[1]).IsEqualTo(1u);
        await Assert.That(args[6]).IsEqualTo(0u);
        await Assert.That(args[11]).IsEqualTo(1u);
        await Assert.That(args[16]).IsEqualTo(0u);
        for (var i = 0; i < bounds.Length; i++)
        {
            var b = bounds[i];
            var visible = b.Visible != 0 && (b.Projected == 0 || !Visibility.IsOccluded(b.Rectangle, b.Nearest, tiles, culling.TilesX));
            await Assert.That(args[i * 5 + 1]).IsEqualTo(visible ? 1u : 0u);
        }
        await Assert.That(pbr.Pipeline.Find<FrustumCullingFeature>()!.CulledDrawCount).IsEqualTo(1);
        scene.Visibility.FrustumEnabled = false;
        scene.Visibility.OcclusionEnabled = false;
        pbr.RenderFrame(scene);
        SamePixels(pixels, backend.ReadbackColor(out _, out _));
        await Assert.That(pixels.Count(x => x > 0)).IsGreaterThan(pixels.Length / 4);
        await Assert.That(pbr.LastPassNames.Any(x => x.StartsWith("Visibility.", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task moving_occluder_and_camera_reveals_geometry_in_the_same_frame()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var culling = pbr.Pipeline.Find<OcclusionCullingFeature>()!;
        pbr.RenderFrame(scene);
        await Assert.That(Arguments(backend, culling)[6]).IsEqualTo(0u);
        scene.Instances[0].Model = Matrix4x4.CreateTranslation(-4, 0, -3);
        scene.Camera.View = Matrix4x4.CreateTranslation(-0.1f, 0.05f, 0);
        pbr.RenderFrame(scene);
        await Assert.That(Arguments(backend, culling)[6]).IsEqualTo(1u);
        var pixels = backend.ReadbackColor(out _, out _).ToArray();
        scene.Visibility.OcclusionEnabled = false;
        pbr.RenderFrame(scene);
        SamePixels(pixels, backend.ReadbackColor(out _, out _));
        scene.Visibility.OcclusionEnabled = true;
        pbr.Switches.Set(PbrFeatures.OcclusionCulling.Id, false);
        pbr.RenderFrame(scene);
        await Assert.That(culling.Active).IsFalse();
        SamePixels(pixels, backend.ReadbackColor(out _, out _));
        pbr.Switches.Set(PbrFeatures.OcclusionCulling.Id, true);
        pbr.RenderFrame(scene);
        await Assert.That(culling.Active).IsTrue();
        await Assert.That(Arguments(backend, culling)[6]).IsEqualTo(1u);
    }

    [Test]
    public async Task alpha_mask_materials_do_not_contribute_occluder_depth()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, mask: true);
        pbr.RenderFrame(scene);
        await Assert.That(Arguments(backend, pbr.Pipeline.Find<OcclusionCullingFeature>()!)[6]).IsEqualTo(1u);
        var pixels = backend.ReadbackColor(out _, out _).ToArray();
        scene.Visibility.OcclusionEnabled = false;
        pbr.RenderFrame(scene);
        SamePixels(pixels, backend.ReadbackColor(out _, out _));
    }

    [Test]
    public async Task dynamic_geometry_uses_a_visible_fallback_for_stale_upload_bounds()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, dynamic: true);
        pbr.RenderFrame(scene);
        var args = Arguments(backend, pbr.Pipeline.Find<OcclusionCullingFeature>()!);
        await Assert.That(args[6]).IsEqualTo(1u);
        await Assert.That(args[16]).IsEqualTo(1u);
        await Assert.That(pbr.Pipeline.Find<FrustumCullingFeature>()!.CulledDrawCount).IsEqualTo(0);
    }

    [Test]
    public async Task invalid_bounds_and_near_plane_crossings_remain_visible()
    {
        var projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f);
        foreach (var (min, max) in new[]
        {
            (new Vector3(float.NaN), Vector3.One),
            (Vector3.One, -Vector3.One),
            (new Vector3(-0.2f, -0.2f, -0.2f), new Vector3(0.2f, 0.2f, -0.05f)),
        })
        {
            await Assert.That(Visibility.IntersectsFrustum(min, max, projection)).IsTrue();
            await Assert.That(Visibility.TryProject(min, max, projection, Size, Size, out _, out _)).IsFalse();
        }
    }

    [Test]
    public async Task resize_partial_tiles_and_empty_frames_do_not_reuse_old_arguments()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var culling = pbr.Pipeline.Find<OcclusionCullingFeature>()!;
        pbr.RenderFrame(scene);
        backend.Resize(131, 117);
        pbr.Resize(131, 117);
        scene.Camera.Projection = PbrMath.Perspective(MathF.PI / 3f, 131f / 117f, 0.1f, 100f);
        pbr.RenderFrame(scene);
        await Assert.That(Arguments(backend, culling)[6]).IsEqualTo(0u);
        var pixels = backend.ReadbackColor(out _, out _).ToArray();
        scene.Visibility.OcclusionEnabled = false;
        pbr.RenderFrame(scene);
        SamePixels(pixels, backend.ReadbackColor(out _, out _));
        scene.Visibility.OcclusionEnabled = true;
        scene.Instances.Clear();
        pbr.RenderFrame(scene);
        await Assert.That(culling.Active).IsFalse();
        await Assert.That(culling.DrawCount).IsEqualTo(0);
        await Assert.That(pbr.LastPassNames.Any(x => x.StartsWith("Visibility.", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task absent_bounds_and_skinned_positions_do_not_use_upload_bounds()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var target = scene.Instances[1].Mesh.Primitives[0];
        var (vertices, indices) = Procedural.UnitCube();
        var jointsWeights = new float[vertices.Length / 12 * 8];
        for (var i = 0; i < jointsWeights.Length / 8; i++) jointsWeights[i * 8 + 4] = 1f;
        var skinned = pbr.UploadSkinnedPrimitive(vertices, jointsWeights, indices, target.MaterialId);
        scene.Instances.Clear();
        scene.Instances.Add(new PbrInstance
        {
            Mesh = new PbrMesh([target with { LocalMin = default, LocalMax = default }]),
            Model = Matrix4x4.CreateScale(0.4f) * Matrix4x4.CreateTranslation(-0.8f, 0, -3),
        });
        // The palette brings a box from outside the camera into view; its original bounds
        // cannot describe the resulting silhouette.
        scene.Instances.Add(new PbrInstance
        {
            Mesh = new PbrMesh([skinned]), JointOffset = 0,
            Model = Matrix4x4.CreateTranslation(0.8f, -100f, -3),
        });
        pbr.SetJointPalette(0, [Matrix4x4.CreateTranslation(0, 100f, 0)]);
        pbr.RenderFrame(scene);
        var culling = pbr.Pipeline.Find<OcclusionCullingFeature>()!;
        var args = Arguments(backend, culling);
        await Assert.That(args[1]).IsEqualTo(1u);
        await Assert.That(args[6]).IsEqualTo(1u);
        await Assert.That(culling.Bounds.ToArray().All(x => x.Projected == 0)).IsTrue();
        await Assert.That(pbr.Pipeline.Find<FrustumCullingFeature>()!.CulledDrawCount).IsEqualTo(0);
        var pixels = backend.ReadbackColor(out _, out _).ToArray();
        scene.Visibility.FrustumEnabled = false;
        scene.Visibility.OcclusionEnabled = false;
        pbr.RenderFrame(scene);
        SamePixels(pixels, backend.ReadbackColor(out _, out _));
        await Assert.That(pixels.Count(x => x > 0)).IsGreaterThan(pixels.Length / 4);
    }

    [Test]
    public async Task transparent_draws_keep_sorting_and_ring_slots_after_culled_opaque_draws()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var (vertices, indices) = Procedural.UnitCube();
        var material = new GltfMaterialData(
            Name: "glass", BaseColorFactor: new Vector4(0.1f, 0.8f, 0.1f, 0.4f),
            MetallicFactor: 0f, RoughnessFactor: 1f, EmissiveFactor: new Vector3(0, 0.5f, 0), NormalScale: 1f,
            OcclusionStrength: 1f, TransmissionFactor: 0f, AlphaMode: GltfAlphaMode.Blend,
            AlphaCutoff: 0.5f, DoubleSided: true, BaseColorImage: -1, MetallicRoughnessImage: -1,
            NormalImage: -1, OcclusionImage: -1, EmissiveImage: -1, BaseColorUvTransform: GltfUvTransform.Identity);
        var first = new PbrMesh([pbr.UploadPrimitive(vertices, indices, pbr.Materials.AddMaterial(material, []))]);
        var second = new PbrMesh([pbr.UploadPrimitive(vertices, indices,
            pbr.Materials.AddMaterial(material with { EmissiveFactor = new Vector3(0, 0, 0.5f) }, []))]);
        scene.Instances.Add(new PbrInstance { Mesh = first, Model = Matrix4x4.CreateTranslation(0, 0, -1.5f) });
        scene.Instances.Add(new PbrInstance { Mesh = second, Model = Matrix4x4.CreateTranslation(0.1f, 0, -2f) });
        scene.Instances.Add(new PbrInstance { Mesh = first, Model = Matrix4x4.CreateTranslation(100f, 0, -1.75f) });
        scene.Ssao = new PbrSsao { Enabled = true };
        pbr.RenderFrame(scene);
        await Assert.That(pbr.Pipeline.Find<FrustumCullingFeature>()!.CulledDrawCount).IsEqualTo(2);
        var pixels = backend.ReadbackColor(out _, out _).ToArray();
        scene.Visibility.FrustumEnabled = false;
        scene.Visibility.OcclusionEnabled = false;
        pbr.RenderFrame(scene);
        SamePixels(pixels, backend.ReadbackColor(out _, out _));
    }

    [Test]
    public async Task frustum_switch_retracts_visibility_and_can_start_disabled()
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        switches.Set(PbrFeatures.FrustumCulling.Id, false);
        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var scene = Scene(pbr);
        scene.Visibility.OcclusionEnabled = false;
        var frustum = pbr.Pipeline.Find<FrustumCullingFeature>()!;
        pbr.RenderFrame(scene);
        await Assert.That(frustum.CulledDrawCount).IsEqualTo(0);
        var pixels = backend.ReadbackColor(out _, out _).ToArray();
        switches.Set(PbrFeatures.FrustumCulling.Id, true);
        pbr.RenderFrame(scene);
        await Assert.That(frustum.CulledDrawCount).IsEqualTo(1);
        SamePixels(pixels, backend.ReadbackColor(out _, out _));
        switches.Set(PbrFeatures.FrustumCulling.Id, false);
        pbr.RenderFrame(scene);
        await Assert.That(frustum.CulledDrawCount).IsEqualTo(0);
        SamePixels(pixels, backend.ReadbackColor(out _, out _));
    }


    [Test]
    public async Task custom_displacement_and_fragment_holes_remain_conservative()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var program = pbr.RegisterMaterialProgram(ShaderProgramLoader.Load(typeof(VisibilityTests).Assembly, "Shaders.visibilityFixture"));
        var material = new GltfMaterialData(
            Name: "displaced-cutout", BaseColorFactor: new Vector4(0, 0.5f, 0, 1),
            MetallicFactor: 0f, RoughnessFactor: 1f, EmissiveFactor: new Vector3(0, 0.3f, 0), NormalScale: 1f,
            OcclusionStrength: 1f, TransmissionFactor: 0f, AlphaMode: GltfAlphaMode.Opaque,
            AlphaCutoff: 0.5f, DoubleSided: false, BaseColorImage: -1, MetallicRoughnessImage: -1,
            NormalImage: -1, OcclusionImage: -1, EmissiveImage: -1, BaseColorUvTransform: GltfUvTransform.Identity);
        var (vertices, indices) = Procedural.UnitCube();
        var wall = pbr.UploadPrimitive(vertices, indices, pbr.Materials.AddMaterial(material, [], program));
        // The vertex shader moves this wall into view, and its fragment stage punches a hole
        // through which the red target must remain visible.
        scene.Instances[0] = new PbrInstance
        {
            Mesh = new PbrMesh([wall]),
            Model = Matrix4x4.CreateScale(2f, 2f, 0.25f) * Matrix4x4.CreateTranslation(0, -4, -3),
        };
        pbr.RenderFrame(scene);
        var args = Arguments(backend, pbr.Pipeline.Find<OcclusionCullingFeature>()!);
        await Assert.That(args[1]).IsEqualTo(1u);
        await Assert.That(args[6]).IsEqualTo(1u);
        await Assert.That(pbr.Pipeline.Find<FrustumCullingFeature>()!.CulledDrawCount).IsEqualTo(1);
        var pixels = backend.ReadbackColor(out _, out _).ToArray();
        scene.Visibility.FrustumEnabled = false;
        scene.Visibility.OcclusionEnabled = false;
        pbr.RenderFrame(scene);
        SamePixels(pixels, backend.ReadbackColor(out _, out _));
        var center = ((int)Size / 2 * (int)Size + (int)Size / 2) * 4;
        var red = backend.ColorFormat is TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb ? center + 2 : center;
        await Assert.That(pixels[red]).IsGreaterThan((byte)150);
        await Assert.That(pixels[center + 1]).IsLessThan((byte)100);
    }

}
