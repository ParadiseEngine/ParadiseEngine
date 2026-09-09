using System.Numerics;
using Paradise.Assets.Gltf;
using System.Runtime.InteropServices;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class DecalTests
{
    private const uint Size = 96;
    [Test]
    public async Task affine_projection_preserves_uv_facing_and_volume_clipping()
    {
        var shear = Matrix4x4.Identity;
        shear.M21 = 0.3f;
        var model = Matrix4x4.CreateScale(-2, 3, 0.5f) * shear * Matrix4x4.CreateRotationY(0.6f)
            * Matrix4x4.CreateTranslation(4, 2, -3);
        var decal = new PbrDecal { Material = new(), Model = model, EdgeFade = 0, DepthFade = 0 };
        Matrix4x4.Invert(model, out var inverse);
        var normal = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, Matrix4x4.Transpose(inverse)));
        await Assert.That(decal.TryProject(Vector3.Transform(new Vector3(0.2f, -0.3f, 0), model), normal, out var uv, out var coverage)).IsTrue();
        await Assert.That(Vector2.Distance(uv, new Vector2(0.7f, 0.8f))).IsLessThan(0.00001f);
        await Assert.That(coverage).IsEqualTo(1f).Within(0.00001f);
        await Assert.That(decal.TryProject(Vector3.Transform(new Vector3(0.6f, 0, 0), model), normal, out _, out _)).IsFalse();
        await Assert.That(decal.TryProject(Vector3.Transform(Vector3.Zero, model), -normal, out _, out _)).IsFalse();
        decal.Model = Matrix4x4.CreateScale(0);
        await Assert.That(decal.TryProject(Vector3.Zero, normal, out _, out _)).IsFalse();
        decal.Model = Matrix4x4.Identity;
        decal.Model.M14 = 0.1f;
        await Assert.That(decal.TryProject(Vector3.Zero, normal, out _, out _)).IsFalse();
    }

    [Test]
    public async Task coverage_combines_opacity_color_alpha_and_edge_depth_fades()
    {
        var decal = new PbrDecal
        {
            Material = new() { Color = new Vector4(1, 1, 1, 0.5f) },
            Opacity = 0.5f, EdgeFade = 0.2f, DepthFade = 0.2f,
        };
        await Assert.That(decal.TryProject(new Vector3(0.4f, 0, 0.4f), Vector3.UnitZ, out _, out var coverage)).IsTrue();
        await Assert.That(coverage).IsEqualTo(0.0625f).Within(0.00001f);
        decal.Opacity = float.NaN;
        await Assert.That(() => decal.TryProject(Vector3.Zero, Vector3.UnitZ, out _, out _)).Throws<ArgumentException>();
    }

    [Test]
    public async Task atlas_mips_preserve_premultiplied_coverage_and_separate_array_layers()
    {
        byte[] source = [255, 0, 0, 255, 0, 255, 0, 0, 255, 0, 0, 255, 0, 255, 0, 0];
        var texture = new PbrDecalTexture(2, 2, source);
        source[0] = 0;
        var atlas = DecalAtlas.Build([new() { ColorTexture = texture }, new() { ColorTexture = new(1, 1, [0, 0, 255, 255]) }], 256);
        await Assert.That(atlas.Layers).IsEqualTo(6);
        await Assert.That(atlas.Mips.Count).IsEqualTo(2);
        var mip = atlas.Mips[1];
        await Assert.That((float)mip[0]).IsEqualTo(0.5f);
        await Assert.That((float)mip[1]).IsEqualTo(0f);
        await Assert.That((float)mip[3]).IsEqualTo(0.5f);
        await Assert.That((float)mip[3 * 4 + 2]).IsEqualTo(1f);
        await Assert.That(Marshal.SizeOf<DecalGpu>()).IsEqualTo(192);
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

    private static PbrScene Scene(PbrRenderer pbr)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, pbr.Materials.AddDefaultMaterial(new Vector4(0.4f, 0.4f, 0.4f, 1)))]);
        var eye = new Vector3(0, 0, 3);
        var scene = new PbrScene
        {
            Camera = new PbrCamera { View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY), Position = eye,
                Projection = PbrMath.Orthographic(4, 1, 0.1f, 20) },
            Ambient = new PbrAmbient { Sky = new Vector3(0.3f), Flat = true },
            Bloom = new PbrBloom { Enabled = false },
            Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
        };
        scene.Instances.Add(new PbrInstance { Mesh = mesh, Model = Matrix4x4.CreateScale(3, 3, 0.2f) });
        scene.Lights.Add(new PbrLight { Type = PbrLightType.Directional,
            Direction = Vector3.Normalize(new Vector3(1, 0, 1)), Intensity = 2 });
        return scene;
    }

    private static byte[] Render(PbrRenderer pbr, WebGpuRenderer backend, PbrScene scene)
    {
        pbr.RenderFrame(scene);
        return backend.ReadbackColor(out _, out _).ToArray();
    }

    private static void Same(byte[] a, byte[] b)
    {
        if (!a.AsSpan().SequenceEqual(b)) throw new InvalidOperationException("Decal disable did not restore the reference image.");
    }

    private static Vector3 Pixel(byte[] pixels, WebGpuRenderer backend, int x, int y)
    {
        var i = (y * (int)Size + x) * 4;
        return backend.ColorFormat is TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb
            ? new Vector3(pixels[i + 2], pixels[i + 1], pixels[i]) : new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task material_channels_change_real_lighting_and_switches_restore_pixels(int channel)
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        switches.Set(PbrFeatures.Decals.Id, false);
        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var scene = Scene(pbr);
        var material = channel switch
        {
            0 => new PbrDecalMaterial { Color = new Vector4(1, 0.05f, 0.05f, 1) },
            1 => new PbrDecalMaterial { ColorWeight = 0, NormalTexture = new(1, 1, [250, 128, 160, 255]) },
            2 => new PbrDecalMaterial { ColorWeight = 0, MaterialWeight = 1, Metallic = 1,
                MetallicRoughnessTexture = new(1, 1, [0, 40, 255, 255]) },
            _ => new PbrDecalMaterial { ColorWeight = 0, EmissionWeight = 1, Emission = new Vector3(0, 0.8f, 0) },
        };
        scene.Decals.Volumes.Add(new PbrDecal { Material = material, Model = Matrix4x4.CreateScale(1.5f, 1.5f, 1) });
        var baseline = Render(pbr, backend, scene);
        switches.Set(PbrFeatures.Decals.Id, true);
        var painted = Render(pbr, backend, scene);
        await Assert.That(painted.Zip(baseline).Count(pair => pair.First != pair.Second)).IsGreaterThan(100);
        await Assert.That(pbr.Pipeline.Find<DecalFeature>()!.ActiveDecalCount).IsEqualTo(1);
        await Assert.That(Pixel(painted, backend, 15, 48)).IsEqualTo(Pixel(baseline, backend, 15, 48));
        switches.Set(PbrFeatures.Decals.Id, false);
        Same(baseline, Render(pbr, backend, scene));
        await Assert.That(pbr.Pipeline.Find<DecalFeature>()!.ActiveDecalCount).IsEqualTo(0);
        switches.Set(PbrFeatures.Decals.Id, true);
        scene.Decals.Enabled = false;
        Same(baseline, Render(pbr, backend, scene));
    }

    [Test]
    public async Task ordered_texture_materials_rebuild_atlas_and_empty_frames_retract_decals()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var baseline = Render(pbr, backend, scene);
        PbrDecalMaterial Paint(byte r, byte g, byte b) => new()
        {
            ColorWeight = 0, EmissionWeight = 1, Emission = Vector3.One,
            ColorTexture = new(2, 1, [r, g, b, 255, r, g, b, 255]),
        };
        var red = new PbrDecal { Material = Paint(255, 0, 0) };
        var green = new PbrDecal { Material = Paint(0, 255, 0) };
        scene.Decals.Volumes.Add(red);
        scene.Decals.Volumes.Add(green);
        var greenPixel = Pixel(Render(pbr, backend, scene), backend, 48, 48);
        await Assert.That(greenPixel.Y).IsGreaterThan(greenPixel.X);
        red.Order = 1;
        var redPixel = Pixel(Render(pbr, backend, scene), backend, 48, 48);
        await Assert.That(redPixel.X).IsGreaterThan(redPixel.Y);
        await Assert.That(pbr.Pipeline.Find<DecalFeature>()!.ResidentMaterialCount).IsEqualTo(2);
        scene.Decals.Volumes.Remove(red);
        var remaining = Pixel(Render(pbr, backend, scene), backend, 48, 48);
        await Assert.That(remaining.Y).IsGreaterThan(remaining.X);
        scene.Decals.Volumes.Clear();
        Same(baseline, Render(pbr, backend, scene));
        await Assert.That(pbr.Pipeline.Find<DecalFeature>()!.ActiveDecalCount).IsEqualTo(0);
    }

    [Test]
    public async Task bound_atlas_view_exposes_generated_lower_mips()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var program = pbr.RegisterMaterialProgram(ShaderProgramLoader.Load(typeof(DecalTests).Assembly, "Shaders.decalMipFixture"));
        var material = new GltfMaterialData("mip probe", Vector4.One, 0, 1, Vector3.Zero, 1, 1,
            0, GltfAlphaMode.Opaque, 0.5f, false, -1, -1, -1, -1, -1, GltfUvTransform.Identity);
        var receiver = scene.Instances[0];
        receiver.Mesh.Primitives[0] = receiver.Mesh.Primitives[0] with { MaterialId = pbr.Materials.AddMaterial(material, [], program) };
        scene.Decals.Volumes.Add(new PbrDecal
        {
            Material = new() { ColorTexture = new(2, 2,
                [255, 0, 0, 255, 0, 0, 255, 255, 0, 0, 255, 255, 255, 0, 0, 255]) },
        });
        var pixel = Pixel(Render(pbr, backend, scene), backend, 48, 48);
        await Assert.That(pixel.X).IsBetween(186f, 190f);
        await Assert.That(pixel.Z).IsBetween(186f, 190f);
        await Assert.That(pixel.Y).IsEqualTo(0f);
    }

    [Test]
    public async Task capacity_counts_valid_volumes_and_rejects_invalid_atlas_settings()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var material = new PbrDecalMaterial();
        scene.Decals.Volumes.Add(new PbrDecal { Material = material, Model = Matrix4x4.CreateScale(0) });
        for (var i = 0; i < PbrDecals.MaxDecals; i++)
            scene.Decals.Volumes.Add(new PbrDecal { Material = material });
        Render(pbr, backend, scene);
        await Assert.That(pbr.Pipeline.Find<DecalFeature>()!.ActiveDecalCount).IsEqualTo(32);
        scene.Decals.Volumes.Add(new PbrDecal { Material = material });
        await Assert.That(() => pbr.RenderFrame(scene)).Throws<InvalidOperationException>();
        scene.Decals.Volumes.Clear();
        scene.Decals.TextureSize = 3;
        await Assert.That(() => pbr.RenderFrame(scene)).Throws<ArgumentException>();
    }

    [Test]
    public async Task instanced_receivers_keep_individual_decal_opt_out()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var receiver = scene.Instances[0];
        receiver.Model = Matrix4x4.CreateScale(1, 1, 0.2f) * Matrix4x4.CreateTranslation(-0.7f, 0, 0);
        scene.Instances.Add(new PbrInstance { Mesh = receiver.Mesh, ReceivesDecals = false,
            Model = Matrix4x4.CreateScale(1, 1, 0.2f) * Matrix4x4.CreateTranslation(0.7f, 0, 0) });
        var baseline = Render(pbr, backend, scene);
        scene.Decals.Volumes.Add(new PbrDecal { Material = new() { Color = new Vector4(1, 0, 0, 1) }, Model = Matrix4x4.CreateScale(4, 4, 1) });
        var painted = Render(pbr, backend, scene);
        await Assert.That(pbr.Pipeline.Find<InstancingFeature>()!.BatchedInstances).IsEqualTo(2);
        await Assert.That(Pixel(painted, backend, 65, 48)).IsEqualTo(Pixel(baseline, backend, 65, 48));
        await Assert.That(Pixel(painted, backend, 31, 48)).IsNotEqualTo(Pixel(baseline, backend, 31, 48));
        scene.Instancing = new PbrInstancing { Enabled = false };
        Same(painted, Render(pbr, backend, scene));
    }
}
