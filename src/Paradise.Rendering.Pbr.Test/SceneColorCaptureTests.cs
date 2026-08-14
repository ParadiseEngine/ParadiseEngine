using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>The scene-color capture seam end to end: enabling capture splits the main pass and
/// leaves opaque-only scenes pixel-identical; a blend material running refractionFixture.slang
/// provably samples the opaque scene BEHIND it through its group-2 extra entry; Resize recreates
/// the view, raises the event, and <see cref="MaterialResourceCache.UpdateExtraEntry"/> rebinds a
/// live material to the new view.</summary>
public class SceneColorCaptureTests
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

    private static PbrScene BuildScene(PbrRenderer pbr, Vector4 opaqueColor)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var materialId = pbr.Materials.AddDefaultMaterial(opaqueColor);
        var mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, materialId)]);

        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(new Vector3(0f, 0.6f, 2.6f), Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                Position = new Vector3(0f, 0.6f, 2.6f),
            },
        };
        scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Directional,
            Direction = Vector3.Normalize(new Vector3(0.4f, 1f, 0.5f)),
            Intensity = 1.2f,
        });
        scene.Instances.Add(new PbrInstance { Mesh = mesh });
        return scene;
    }

    private static GltfMaterialData BlendMaterial(uint width, uint height) => new(
        Name: "capture-consumer",
        BaseColorFactor: Vector4.One,
        MetallicFactor: 0f,
        RoughnessFactor: 0.5f,
        EmissiveFactor: Vector3.Zero,
        NormalScale: 1f,
        OcclusionStrength: 1f,
        TransmissionFactor: 0f,
        AlphaMode: GltfAlphaMode.Blend,
        AlphaCutoff: 0.5f,
        DoubleSided: true,
        BaseColorImage: -1,
        MetallicRoughnessImage: -1,
        NormalImage: -1,
        OcclusionImage: -1,
        EmissiveImage: -1,
        BaseColorUvTransform: GltfUvTransform.Identity)
    {
        // The fixture reads the screen size from the free procColorA lanes (ProcKind stays 0).
        ProcColorA = new Vector3(width, height, 0f),
    };

    [Test]
    public async Task capture_is_pixel_neutral_for_scenes_without_blend_materials()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            var scene = BuildScene(pbr, new Vector4(0.2f, 0.7f, 0.3f, 1f));

            for (var i = 0; i < 2; i++) pbr.RenderFrame(scene);
            var off = (byte[])renderer.ReadbackColor(out _, out _).Clone();

            pbr.SceneColorCapture = true;
            await Assert.That(pbr.SceneColorView.IsValid).IsTrue();
            for (var i = 0; i < 2; i++) pbr.RenderFrame(scene);
            var on = renderer.ReadbackColor(out _, out _);

            // The split (Clear A / blit / Load B) must not change a single pixel of an
            // opaque-only frame — the same draws hit the same targets with the same state.
            await Assert.That(off.AsSpan().SequenceEqual(on)).IsTrue();

            pbr.SceneColorCapture = false;
            await Assert.That(pbr.SceneColorView.IsValid).IsFalse();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task a_blend_material_samples_the_opaque_scene_behind_it()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            pbr.SceneColorCapture = true;

            var program = ShaderProgramLoader.Load(typeof(SceneColorCaptureTests).Assembly, "Shaders.refractionFixture");
            var programId = pbr.RegisterMaterialProgram(program);
            var material = BlendMaterial(64, 64);
            var materialId = pbr.Materials.AddMaterial(in material, [], programId,
                [BindGroupEntryDesc.ForTextureView(7, pbr.SceneColorView)]);

            // A RED opaque cube behind, and a capture-consuming blend quad in front of the camera.
            var scene = BuildScene(pbr, new Vector4(0.9f, 0.05f, 0.05f, 1f));
            var (vertices, indices) = Procedural.UnitCube();
            var quadMesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, materialId)]);
            scene.Instances.Add(new PbrInstance
            {
                Mesh = quadMesh,
                Model = Matrix4x4.CreateScale(new Vector3(1.2f, 1.2f, 0.02f))
                    * Matrix4x4.CreateTranslation(0f, 0.4f, 1.2f),
            });

            for (var i = 0; i < 3; i++) pbr.RenderFrame(scene);
            var pixels = renderer.ReadbackColor(out var w, out var h);

            // Center pixel: the blend quad covers it, and the fixture emits the CAPTURED color at
            // this exact screen position — the red cube. If capture were broken (zero texture,
            // stale view, wrong pass order) this reads black or clear-color blue instead.
            var idx = (int)((h / 2) * w + (w / 2)) * 4;
            var b = pixels[idx + 0];
            var r = pixels[idx + 2];
            await Assert.That((int)r).IsGreaterThan(120);
            await Assert.That((int)r).IsGreaterThan(b + 40);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task resize_recreates_the_view_raises_the_event_and_update_extra_entry_rebinds()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            pbr.SceneColorCapture = true;
            var oldView = pbr.SceneColorView;

            var program = ShaderProgramLoader.Load(typeof(SceneColorCaptureTests).Assembly, "Shaders.refractionFixture");
            var programId = pbr.RegisterMaterialProgram(program);
            var material = BlendMaterial(96, 96);
            var materialId = pbr.Materials.AddMaterial(in material, [], programId,
                [BindGroupEntryDesc.ForTextureView(7, pbr.SceneColorView)]);

            var raised = 0;
            pbr.SceneColorViewChanged += () => raised++;

            renderer.Resize(96, 96);
            pbr.Resize(96, 96);

            await Assert.That(raised).IsEqualTo(1);
            await Assert.That(pbr.SceneColorView.IsValid).IsTrue();
            await Assert.That(pbr.SceneColorView).IsNotEqualTo(oldView);

            // The game-side rebind the event exists for — then the frame renders with the fresh
            // view (an unbound stale view would surface as a Dawn error / dropped draws).
            pbr.Materials.UpdateExtraEntry(materialId, BindGroupEntryDesc.ForTextureView(7, pbr.SceneColorView));
            var scene = BuildScene(pbr, new Vector4(0.2f, 0.7f, 0.3f, 1f));
            pbr.RenderFrame(scene);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task disabling_capture_raises_the_event_with_an_invalid_view()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            pbr.SceneColorCapture = true;

            var raised = 0;
            var viewValidInHandler = true;
            pbr.SceneColorViewChanged += () =>
            {
                raised++;
                viewValidInHandler = pbr.SceneColorView.IsValid;
            };

            // Disable must notify too — a subscriber still bound to the old view is otherwise
            // left holding a bind group over a destroyed resource. In the handler the view is
            // already INVALID: unbind/repoint, never re-bind it.
            pbr.SceneColorCapture = false;
            await Assert.That(raised).IsEqualTo(1);
            await Assert.That(viewValidInHandler).IsFalse();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task update_extra_entry_validates_binding_and_kind()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            using var pbr = new PbrRenderer(renderer, 64, 64);
            pbr.SceneColorCapture = true;
            var program = ShaderProgramLoader.Load(typeof(SceneColorCaptureTests).Assembly, "Shaders.refractionFixture");
            var programId = pbr.RegisterMaterialProgram(program);
            var material = BlendMaterial(64, 64);
            var materialId = pbr.Materials.AddMaterial(in material, [], programId,
                [BindGroupEntryDesc.ForTextureView(7, pbr.SceneColorView)]);

            // Standard slots are off limits.
            await Assert.That(() => pbr.Materials.UpdateExtraEntry(
                materialId, BindGroupEntryDesc.ForTextureView(2, pbr.SceneColorView))).Throws<ArgumentException>();
            // Unknown extra binding.
            await Assert.That(() => pbr.Materials.UpdateExtraEntry(
                materialId, BindGroupEntryDesc.ForTextureView(9, pbr.SceneColorView))).Throws<ArgumentException>();
            // Kind mismatch: a sampler where the program declares a texture.
            var sampler = renderer.CreateSampler(new SamplerDesc(
                "WrongKind",
                SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
                SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest, 1));
            await Assert.That(() => pbr.Materials.UpdateExtraEntry(
                materialId, BindGroupEntryDesc.ForSampler(7, sampler))).Throws<ArgumentException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }
}
