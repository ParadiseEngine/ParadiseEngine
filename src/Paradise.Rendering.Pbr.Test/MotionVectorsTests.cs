using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.Rendering.Graph;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class MotionVectorsTests
{
    private const uint Size = 64;

    private sealed class ProbeFeature : IRenderFeature
    {
        private readonly WebGpuRenderer _renderer;
        private readonly ShaderProgramDesc _program;
        private readonly PipelineHandle _pipeline;

        public ProbeFeature(WebGpuRenderer renderer)
        {
            _renderer = renderer;
            _program = ShaderProgramLoader.Load(typeof(MotionVectorsTests).Assembly, "Shaders.motionProbeFixture");
            _pipeline = renderer.CreatePipeline(_program, renderer.ColorFormat);
        }

        public FeatureDefinition Definition { get; } = new("test.motionProbe", true, "Displays motion.");
        public FrameRequirements Requires => FrameRequirements.MotionVectors;
        public void Resize(uint width, uint height) { }
        public void Setup(in FrameContext frame)
        {
            if (!frame.Blackboard.TryGet(PbrResults.MotionVectors, out var motion)) return;
            frame.Graph.AddRasterPass("Test.MotionProbe", RenderPassEvent.Overlay)
                .Color(0, FrameGraph.Backbuffer, LoadOp.Clear)
                .BindGroup(0, "Test.MotionProbe", ShaderPrograms.FindGroup(_program, 0), [GraphBinding.Texture(0, motion)])
                .Record(this, Record);
        }

        private static void Record(ProbeFeature self, ref PassRecording pass, int _)
        {
            pass.Encoder.SetPipeline(self._pipeline);
            pass.SetBindGroup(0);
            pass.Encoder.Draw(new DrawCommand(3, 1, 0, 0));
        }

        public void Dispose() => _renderer.DestroyPipeline(_pipeline);
    }

    private static WebGpuRenderer? Backend()
    {
        try { return WebGpuRenderer.CreateHeadless(Size, Size); }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter available: {error.Message}");
            return null;
        }
    }

    private static PbrScene Scene(PbrRenderer pbr, bool skinned = false)
    {
        float[] vertices =
        [
            -0.5f, -0.5f, 0.5f, 0, 0, 1, 0, 0, 1, 0, 0, 1,
             0.5f, -0.5f, 0.5f, 0, 0, 1, 1, 0, 1, 0, 0, 1,
             0.5f,  0.5f, 0.5f, 0, 0, 1, 1, 1, 1, 0, 0, 1,
            -0.5f,  0.5f, 0.5f, 0, 0, 1, 0, 1, 1, 0, 0, 1,
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var primitive = skinned
            ? pbr.UploadSkinnedPrimitive(vertices,
                [0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0,
                 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0], indices, material)
            : pbr.UploadPrimitive(vertices, indices, material);
        var scene = new PbrScene
        {
            Camera = new PbrCamera { View = Matrix4x4.Identity, Projection = Matrix4x4.Identity },
        };
        scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh([primitive]), JointOffset = skinned ? 0 : -1 });
        return scene;
    }

    private static Vector4 Pixel(WebGpuRenderer backend, uint x, uint y)
    {
        var pixels = backend.ReadbackColor(out var width, out _);
        var offset = (int)((y * width + x) * 4);
        var red = backend.ColorFormat is TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb ? 2 : 0;
        return new Vector4(pixels[offset + red], pixels[offset + 1], pixels[offset + 2 - red], pixels[offset + 3]);
    }

    private static Vector4 Center(WebGpuRenderer backend) => Pixel(backend, Size / 2, Size / 2);

    [Test]
    public async Task Object_motion_has_top_left_uv_sign_and_retains_previous_depth()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        pbr.Pipeline.Add(new ProbeFeature(backend));
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).Z).IsEqualTo(0f);
        pbr.RenderFrame(scene);
        var stationary = Center(backend);
        await Assert.That(stationary.X).IsBetween(127f, 129f);
        await Assert.That(stationary.Z).IsEqualTo(255f);

        scene.Instances[0].Model = Matrix4x4.CreateTranslation(0.25f, 0.125f, 0f);
        pbr.RenderFrame(scene);
        var moving = Center(backend);
        // delta UV = (0.125, -0.0625): encoded as (0.75, 0.375), not pixels or NDC.
        await Assert.That(moving.X).IsBetween(190f, 192f);
        await Assert.That(moving.Y).IsBetween(95f, 97f);
        await Assert.That(moving.W).IsBetween(127f, 129f);
    }

    [Test]
    public async Task Camera_motion_and_projection_jitter_are_included()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        pbr.Pipeline.Add(new ProbeFeature(backend));
        pbr.RenderFrame(scene);
        scene.Camera.View = Matrix4x4.CreateTranslation(-0.125f, 0f, 0f);
        scene.Camera.Projection = Matrix4x4.CreateTranslation(0f, 0.125f, 0f);
        pbr.RenderFrame(scene);
        var motion = Center(backend);
        await Assert.That(motion.X).IsBetween(95f, 97f);
        await Assert.That(motion.Y).IsBetween(95f, 97f);
        await Assert.That(motion.Z).IsEqualTo(255f);
    }

    [Test]
    public async Task Perspective_motion_matches_reprojection_of_the_visible_surface_point()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        pbr.Pipeline.Add(new ProbeFeature(backend));
        scene.Camera.Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 10f);
        var pivot = Matrix4x4.CreateTranslation(0f, 0f, -0.5f);
        var previousModel = pivot * Matrix4x4.CreateRotationY(-0.6f) * Matrix4x4.CreateTranslation(0f, 0f, -1.5f);
        var currentModel = pivot * Matrix4x4.CreateRotationY(0.8f) * Matrix4x4.CreateTranslation(0f, 0f, -1.5f);
        scene.Instances[0].Model = previousModel;
        pbr.RenderFrame(scene);
        scene.Instances[0].Model = currentModel;
        pbr.RenderFrame(scene);

        var pixelCenter = new Vector2(Size / 2 + 6.5f, Size / 2 + 0.5f);
        // Ray/plane intersection finds the local point independently of raster interpolation.
        await Assert.That(PbrMath.TryScreenPointToRay(pixelCenter, new Vector2(Size),
            currentModel * scene.Camera.Projection, out var origin, out var direction)).IsTrue();
        var local = origin + direction * ((0.5f - origin.Z) / direction.Z);
        var previousClip = Vector4.Transform(new Vector4(local, 1f), previousModel * scene.Camera.Projection);
        var previousUv = new Vector2(previousClip.X, -previousClip.Y) / previousClip.W * 0.5f + new Vector2(0.5f);
        var expected = ((pixelCenter / Size - previousUv) * 2f + new Vector2(0.5f)) * 255f;
        var actual = Pixel(backend, Size / 2 + 6, Size / 2);
        await Assert.That(actual.X).IsBetween(expected.X - 2f, expected.X + 2f);
        await Assert.That(actual.Y).IsBetween(expected.Y - 2f, expected.Y + 2f);
        await Assert.That(actual.Z).IsEqualTo(255f);
        await Assert.That(actual.W).IsBetween(previousClip.Z / previousClip.W * 255f - 1f,
            previousClip.Z / previousClip.W * 255f + 1f);
    }

    [Test]
    public async Task Skin_deformation_uses_the_previous_palette_even_when_palette_offset_changes()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, skinned: true);
        pbr.Pipeline.Add(new ProbeFeature(backend));
        pbr.SetJointPalette(0, [Matrix4x4.Identity]);
        pbr.RenderFrame(scene);
        pbr.SetJointPalette(4, [Matrix4x4.CreateTranslation(0.25f, 0f, 0f)]);
        // Overwriting the old offset proves previous data does not alias this frame's palette.
        pbr.SetJointPalette(0, [Matrix4x4.CreateTranslation(-0.5f, 0f, 0f)]);
        scene.Instances[0].JointOffset = 4;
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).X).IsBetween(190f, 192f);
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).X).IsBetween(127f, 129f);
    }

    [Test]
    public async Task Cuts_resize_and_switch_transitions_reject_stale_history()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        pbr.Pipeline.Add(new ProbeFeature(backend));
        var feature = pbr.Pipeline.Find<MotionVectorsFeature>()!;
        pbr.RenderFrame(scene);
        pbr.RenderFrame(scene);
        await Assert.That(feature.HistoryReady).IsTrue();
        scene.TemporalHistoryVersion++;
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).Z).IsEqualTo(0f);
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).Z).IsEqualTo(255f);
        feature.ResetHistory();
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).Z).IsEqualTo(0f);
        pbr.Resize(Size + 2, Size + 2);
        pbr.Resize(Size, Size);
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).Z).IsEqualTo(0f);
        pbr.Switches.Set(PbrFeatures.MotionVectors.Id, false);
        pbr.RenderFrame(scene);
        await Assert.That(pbr.LastPassNames).DoesNotContain("MotionVectors.Geometry");
        await Assert.That(feature.View.IsValid).IsFalse();
        pbr.Switches.Set(PbrFeatures.MotionVectors.Id, true);
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).Z).IsEqualTo(0f);
    }

    [Test]
    public async Task Restart_preserves_palettes_staged_while_motion_was_inactive()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, skinned: true);
        pbr.Pipeline.Add(new ProbeFeature(backend));
        // Unstaged slots already contain the identity pose in the current GPU palette.
        pbr.RenderFrame(scene);
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).Z).IsEqualTo(255f);
        await Assert.That(Center(backend).X).IsBetween(127f, 129f);

        pbr.Switches.Set(PbrFeatures.MotionVectors.Id, false);
        pbr.SetJointPalette(0, [Matrix4x4.CreateTranslation(0.25f, 0f, 0f)]);
        pbr.RenderFrame(scene);
        pbr.Switches.Set(PbrFeatures.MotionVectors.Id, true);
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).Z).IsEqualTo(0f);
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).X).IsBetween(127f, 129f);
        await Assert.That(Center(backend).Z).IsEqualTo(255f);
    }

    [Test]
    public async Task Background_new_instances_and_visible_transparency_reject_history()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        pbr.Pipeline.Add(new ProbeFeature(backend));
        pbr.RenderFrame(scene);
        pbr.RenderFrame(scene);
        await Assert.That(Pixel(backend, 1, 1).Z).IsEqualTo(0f);
        await Assert.That(Center(backend).Z).IsEqualTo(255f);

        scene.Instances[0] = new PbrInstance { Mesh = scene.Instances[0].Mesh };
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).Z).IsEqualTo(0f);
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).Z).IsEqualTo(255f);

        var blend = pbr.Materials.AddMaterial(new GltfMaterialData(
            Name: "Motion rejection glass",
            BaseColorFactor: new Vector4(1f, 1f, 1f, 0.5f), MetallicFactor: 0f, RoughnessFactor: 1f, TransmissionFactor: 0f,
            BaseColorImage: -1, MetallicRoughnessImage: -1, NormalImage: -1, NormalScale: 1f,
            OcclusionImage: -1, OcclusionStrength: 1f, EmissiveImage: -1, EmissiveFactor: Vector3.Zero,
            AlphaMode: GltfAlphaMode.Blend, AlphaCutoff: 0.5f, DoubleSided: false,
            BaseColorUvTransform: GltfUvTransform.Identity), []);
        var glass = new PbrInstance
        {
            Mesh = new PbrMesh([scene.Instances[0].Mesh.Primitives[0] with { MaterialId = blend }]),
            Model = Matrix4x4.CreateTranslation(0f, 0f, -0.1f),
        };
        scene.Instances.Add(glass);
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).Z).IsEqualTo(0f);
        glass.Model = Matrix4x4.CreateTranslation(0f, 0f, 0.1f);
        pbr.RenderFrame(scene);
        await Assert.That(Center(backend).Z).IsEqualTo(255f);
    }

    [Test]
    public async Task Motion_is_lazy_until_requested_and_scene_opt_out_restarts_history()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var feature = pbr.Pipeline.Find<MotionVectorsFeature>()!;
        pbr.RenderFrame(scene);
        await Assert.That(feature.View.IsValid).IsFalse();
        await Assert.That(pbr.LastPassNames).DoesNotContain("MotionVectors.Geometry");
        scene.MotionVectors = new PbrMotionVectors { Enabled = true };
        pbr.RenderFrame(scene);
        await Assert.That(feature.View.IsValid).IsTrue();
        await Assert.That(pbr.LastPassNames).Contains("MotionVectors.Geometry");
        pbr.RenderFrame(scene);
        await Assert.That(feature.HistoryReady).IsTrue();
        scene.MotionVectors = new PbrMotionVectors();
        pbr.RenderFrame(scene);
        await Assert.That(feature.HistoryReady).IsFalse();
        await Assert.That(feature.View.IsValid).IsFalse();
        scene.MotionVectors = new PbrMotionVectors { Enabled = true };
        pbr.RenderFrame(scene);
        await Assert.That(feature.HistoryReady).IsFalse();
    }

    [Test]
    public async Task History_tracks_instance_identity_and_discards_missing_objects_and_scenes()
    {
        var history = new MotionHistory();
        var scene = new PbrScene();
        var a = new PbrInstance { Mesh = new PbrMesh([]) };
        var b = new PbrInstance { Mesh = a.Mesh };
        var primitive = new PbrPrimitive(default, default, 0, 0, 0, 0);
        await Assert.That(history.Begin(scene)).IsFalse();
        history.Capture(scene, Matrix4x4.Identity, [(a, primitive, 0f), (b, primitive, 0f)]);
        a.Model = Matrix4x4.CreateTranslation(1f, 0f, 0f);
        await Assert.That(history.Begin(scene)).IsTrue();
        await Assert.That(history.TryPrevious(a, out var previous)).IsTrue();
        await Assert.That(previous.Model).IsEqualTo(Matrix4x4.Identity);
        history.Capture(scene, Matrix4x4.Identity, [(b, primitive, 0f)]);
        await Assert.That(history.TryPrevious(a, out _)).IsFalse();
        await Assert.That(history.Begin(new PbrScene())).IsFalse();
        await Assert.That(history.TryPrevious(b, out _)).IsFalse();
    }
}
