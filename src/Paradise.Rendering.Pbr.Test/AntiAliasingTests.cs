using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Rendering.Graph;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class AntiAliasingTests
{
    private const uint Size = 64;

    private sealed class SignalFeature : IRenderFeature
    {
        private const string Target = "Test.AaSignal";
        private readonly WebGpuRenderer _renderer;
        private readonly ShaderProgramDesc _program;
        private readonly PipelineHandle _pipeline;
        private readonly BufferHandle _uniform;
        public Vector4 Signal = new(0.3f, 0.2f, 2f, 0f);

        public SignalFeature(WebGpuRenderer renderer)
        {
            _renderer = renderer;
            _program = ShaderProgramLoader.Load(typeof(AntiAliasingTests).Assembly, "Shaders.antialiasingFixture");
            _pipeline = renderer.CreatePipeline(_program, TextureFormat.Rgba16Float);
            _uniform = renderer.CreateBuffer(new BufferDesc("Test.AaSignal", 16, BufferUsage.Uniform | BufferUsage.CopyDst));
        }
        public FeatureDefinition Definition { get; } = new("test.aaSignal", true);
        public FrameRequirements Requires => FrameRequirements.None;
        public void Resize(uint width, uint height) { }
        public void Setup(in FrameContext frame)
        {
            _renderer.UpdateBuffer<Vector4>(_uniform, 0, MemoryMarshal.CreateReadOnlySpan(ref Signal, 1));
            frame.Textures.Ensure(Target, PbrTargets.RenderTarget(frame.Width, frame.Height, TextureFormat.Rgba16Float));
            var source = frame.Blackboard.GetOrDefault(PbrResults.SceneColor, frame.Graph.Texture(PbrTargets.Hdr));
            var output = frame.Graph.Texture(Target);
            frame.Graph.AddRasterPass("Test.AaSignal", RenderPassEvent.AfterTransparent, offset: 10)
                .Color(0, output, LoadOp.Clear)
                .BindGroup(0, "Test.AaSignal", ShaderPrograms.FindGroup(_program, 0), [GraphBinding.Buffer(0, _uniform, 0, 16)])
                .Record(this, Record);
            frame.Blackboard.Advance(PbrResults.SceneColor, source, output);
        }
        private static void Record(SignalFeature self, ref PassRecording pass, int _) => Fullscreen.Record(ref pass, self._pipeline);
        public void Dispose()
        {
            _renderer.DestroyPipeline(_pipeline);
            _renderer.DestroyBuffer(_uniform);
        }
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

    private static PbrScene Scene(PbrRenderer pbr, bool triangle = false, bool skinned = false)
    {
        float[] vertices = triangle
            ? [-0.9f, -0.8f, 0.5f, 0, 0, 1, 0, 0, 1, 0, 0, 1,
                0.9f, -0.75f, 0.5f, 0, 0, 1, 1, 0, 1, 0, 0, 1,
               -0.6f, 0.85f, 0.5f, 0, 0, 1, 0, 1, 1, 0, 0, 1]
            : [-1, -1, 0.5f, 0, 0, 1, 0, 0, 1, 0, 0, 1,
                1, -1, 0.5f, 0, 0, 1, 1, 0, 1, 0, 0, 1,
                1, 1, 0.5f, 0, 0, 1, 1, 1, 1, 0, 0, 1,
               -1, 1, 0.5f, 0, 0, 1, 0, 1, 1, 0, 0, 1];
        uint[] indices = triangle ? [0, 1, 2] : [0, 1, 2, 0, 2, 3];
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One, metallic: 0f, roughness: 1f);
        PbrPrimitive primitive;
        if (skinned)
        {
            var joints = new float[vertices.Length / 12 * 8];
            for (var i = 4; i < joints.Length; i += 8) joints[i] = 1f;
            primitive = pbr.UploadSkinnedPrimitive(vertices, joints, indices, material);
        }
        else primitive = pbr.UploadPrimitive(vertices, indices, material);
        var scene = new PbrScene
        {
            Camera = new PbrCamera { View = Matrix4x4.Identity, Projection = Matrix4x4.Identity, Position = Vector3.UnitZ },
            ClearColor = new ColorRgba(0f, 0f, 0f, 1f),
            Ambient = new PbrAmbient { Flat = true, Sky = Vector3.One },
            Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
        };
        scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh([primitive]), JointOffset = skinned ? 0 : -1 });
        return scene;
    }

    private static byte[] Capture(WebGpuRenderer backend) => (byte[])backend.ReadbackColor(out _, out _).Clone();
    private static int Center(byte[] pixels) => pixels[(int)((Size / 2 * Size + Size / 2) * 4)];
    private static float Encode(float linear) => (linear <= 0.0031308f ? 12.92f * linear : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f) * 255f;
    private static float Decode(int encoded)
    {
        var value = encoded / 255f;
        return value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }
    private static int FractionalPixels(byte[] pixels, int maximum = 255) =>
        pixels.Where((value, index) => index % 4 == 0 && value > 3 && value < maximum - 3).Count();
    private static SignalFeature AddSignal(PbrRenderer pbr, WebGpuRenderer backend)
    {
        var feature = new SignalFeature(backend);
        pbr.Pipeline.Add(feature, PbrFeatureOrder.TemporalAntiAliasing - 1);
        return feature;
    }

    [Test]
    public async Task Fxaa_filters_diagonal_edges_and_preserves_uniform_color_and_output_gamma()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var signal = AddSignal(pbr, backend);
        signal.Signal = new Vector4(0, 0, 1, 0);
        pbr.RenderFrame(scene);
        var off = Capture(backend);
        scene.Fxaa = new PbrFxaa { Enabled = true };
        pbr.RenderFrame(scene);
        var on = Capture(backend);
        await Assert.That(FractionalPixels(off)).IsEqualTo(0);
        await Assert.That(FractionalPixels(on)).IsGreaterThan(30);
        await Assert.That(pbr.LastPassNames).Contains("Fxaa.Resolve");
        await Assert.That(pbr.LastPassNames).Contains("Presentation");

        signal.Signal = new Vector4(0.25f, 0, 0, 0);
        pbr.RenderFrame(scene);
        await Assert.That(Center(Capture(backend))).IsBetween((int)Encode(0.25f) - 1, (int)Encode(0.25f) + 1);
        var uniformOn = Capture(backend);
        scene.Fxaa = new PbrFxaa();
        pbr.RenderFrame(scene);
        var uniformOff = Capture(backend);
        await Assert.That(uniformOn.Zip(uniformOff, (a, b) => Math.Abs(a - b)).Max()).IsLessThanOrEqualTo(1);
        await Assert.That(pbr.LastPassNames).DoesNotContain("Fxaa.Resolve");
        await Assert.That(pbr.LastPassNames).DoesNotContain("Presentation");
    }

    [Test]
    public async Task Taa_accumulates_real_jittered_geometry_and_keeps_the_authored_camera()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, triangle: true);
        var projection = scene.Camera.Projection;
        pbr.RenderFrame(scene);
        var off = Capture(backend);
        var maximum = off.Where((_, i) => i % 4 == 0).Max();
        scene.Taa = new PbrTaa { Enabled = true };
        var temporal = pbr.Pipeline.Find<TemporalAntiAliasingFeature>()!;
        for (var i = 0; i < 8; i++) pbr.RenderFrame(scene);
        var on = Capture(backend);
        await Assert.That(FractionalPixels(on, maximum)).IsGreaterThan(FractionalPixels(off, maximum) + 20);
        await Assert.That(temporal.HistoryReady).IsTrue();
        await Assert.That(scene.Camera.Projection).IsEqualTo(projection);
        await Assert.That(temporal.JitterPixels).IsEqualTo(AntiAliasingMath.Jitter(7));
        await Assert.That(pbr.LastPassNames).Contains("MotionVectors.Geometry");
        await Assert.That(pbr.LastPassNames).Contains("Taa.Resolve");
    }

    [Test]
    public async Task Taa_reuses_HDR_history_before_exposure_and_clamps_changed_neighborhoods()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var signal = AddSignal(pbr, backend);
        scene.Taa = new PbrTaa { Enabled = true, JitterScale = 0f };
        scene.Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear, Exposure = 0.1f };
        signal.Signal = new Vector4(2, 1, 2, 0);
        pbr.RenderFrame(scene);
        signal.Signal.X = 3f;
        pbr.RenderFrame(scene);
        var blended = Decode(Center(Capture(backend)));
        await Assert.That(blended).IsBetween(0.30f, 0.34f);
        signal.Signal = new Vector4(0.2f, 0f, 0f, 0f);
        pbr.RenderFrame(scene);
        await Assert.That(Decode(Center(Capture(backend)))).IsBetween(0.018f, 0.023f);
    }

    [Test]
    public async Task Taa_rejects_disoccluded_history_using_saved_depth()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var signal = AddSignal(pbr, backend);
        scene.Taa = new PbrTaa { Enabled = true, JitterScale = 0f };
        var front = new PbrInstance { Mesh = scene.Instances[0].Mesh, Model = Matrix4x4.CreateTranslation(0, 0, -0.3f) };
        scene.Instances.Add(front);
        pbr.RenderFrame(scene);
        scene.Instances.Remove(front);
        signal.Signal.X = 0.4f;
        pbr.RenderFrame(scene);
        await Assert.That(Decode(Center(Capture(backend)))).IsBetween(0.59f, 0.61f);
        // Once the newly exposed depth is saved, the same surface can accumulate normally.
        signal.Signal.X = 0.5f;
        pbr.RenderFrame(scene);
        await Assert.That(Decode(Center(Capture(backend)))).IsBetween(0.59f, 0.64f);
    }

    [Test]
    public async Task Taa_cuts_resize_scene_changes_and_enable_transitions_restart_from_current_color()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        var signal = AddSignal(pbr, backend);
        scene.Taa = new PbrTaa { Enabled = true, JitterScale = 0f };
        var temporal = pbr.Pipeline.Find<TemporalAntiAliasingFeature>()!;
        for (var scenario = 0; scenario < 7; scenario++)
        {
            signal.Signal.X = 0.3f;
            pbr.RenderFrame(scene);
            pbr.RenderFrame(scene);
            await Assert.That(temporal.HistoryReady).IsTrue();
            switch (scenario)
            {
                case 0: scene.TemporalHistoryVersion++; break;
                case 1: temporal.ResetHistory(); break;
                case 2: pbr.Resize(Size + 2, Size + 2); pbr.Resize(Size, Size); break;
                case 3:
                    pbr.Switches.Set(PbrFeatures.TemporalAntiAliasing.Id, false);
                    pbr.RenderFrame(scene);
                    pbr.Switches.Set(PbrFeatures.TemporalAntiAliasing.Id, true);
                    break;
                case 4:
                    scene.Taa = scene.Taa with { Enabled = false };
                    pbr.RenderFrame(scene);
                    scene.Taa = scene.Taa with { Enabled = true };
                    break;
                case 5:
                    var replacement = new PbrScene { Camera = scene.Camera, Taa = scene.Taa, Tonemap = scene.Tonemap };
                    replacement.Instances.AddRange(scene.Instances);
                    scene = replacement;
                    break;
                case 6:
                    pbr.Switches.Set(PbrFeatures.MotionVectors.Id, false);
                    pbr.RenderFrame(scene);
                    await Assert.That(pbr.LastPassNames).DoesNotContain("Taa.Resolve");
                    await Assert.That(temporal.JitterPixels).IsEqualTo(Vector2.Zero);
                    pbr.Switches.Set(PbrFeatures.MotionVectors.Id, true);
                    break;
            }
            signal.Signal.X = 0.5f;
            pbr.RenderFrame(scene);
            await Assert.That(temporal.HistoryReady).IsFalse();
            await Assert.That(Decode(Center(Capture(backend)))).IsBetween(0.69f, 0.71f);
        }
    }

    [Test]
    public async Task Motion_reset_and_out_of_bounds_camera_object_and_skin_reprojection_reject_history()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, skinned: true);
        var signal = AddSignal(pbr, backend);
        scene.Taa = new PbrTaa { Enabled = true, JitterScale = 0f };
        for (var scenario = 0; scenario < 4; scenario++)
        {
            scene.Camera.View = Matrix4x4.Identity;
            scene.Instances[0].Model = Matrix4x4.CreateScale(2f);
            pbr.SetJointPalette(0, [Matrix4x4.Identity]);
            scene.TemporalHistoryVersion++;
            signal.Signal.X = 0.3f;
            pbr.RenderFrame(scene);
            if (scenario == 0) pbr.Pipeline.Find<MotionVectorsFeature>()!.ResetHistory();
            // Rendering a formerly offscreen surface maps the center outside the history image.
            if (scenario == 1) scene.Camera.View = Matrix4x4.CreateTranslation(-1.2f, 0, 0);
            if (scenario == 2) scene.Instances[0].Model = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(-1.2f, 0, 0);
            if (scenario == 3) pbr.SetJointPalette(0, [Matrix4x4.CreateTranslation(-0.6f, 0, 0)]);
            signal.Signal.X = 0.5f;
            pbr.RenderFrame(scene);
            await Assert.That(Decode(Center(Capture(backend)))).IsBetween(0.69f, 0.71f);
        }
    }

    [Test]
    public async Task Jitter_offsets_clip_space_by_pixel_units_for_perspective_and_orthographic_cameras()
    {
        var jitter = new Vector2(0.25f, -0.375f);
        foreach (var projection in new[] { Matrix4x4.Identity, PbrMath.Perspective(1.1f, 1.3f, 0.1f, 100f) })
        {
            var position = new Vector4(0.3f, 0.1f, -2f, 1f);
            var before = Vector4.Transform(position, projection);
            var after = Vector4.Transform(position, AntiAliasingMath.JitterProjection(projection, jitter, 320, 240));
            var delta = (new Vector2(after.X, -after.Y) / after.W - new Vector2(before.X, -before.Y) / before.W) * 0.5f;
            await Assert.That(Vector2.Distance(delta, jitter / new Vector2(320, 240))).IsLessThan(1e-6f);
            await Assert.That(after.Z).IsEqualTo(before.Z);
            await Assert.That(after.W).IsEqualTo(before.W);
        }
        await Assert.That(Enumerable.Range(0, 8).Select(i => AntiAliasingMath.Jitter((uint)i)).Distinct().Count()).IsEqualTo(8);
        await Assert.That(AntiAliasingMath.Jitter(8)).IsEqualTo(AntiAliasingMath.Jitter(0));
    }

    [Test]
    public async Task Jittered_local_light_binning_matches_unbinned_rendering()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr);
        scene.Camera.Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 20f);
        scene.Camera.Position = Vector3.Zero;
        scene.Instances[0].Model = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(0, 0, -3f);
        scene.Ambient = new PbrAmbient { Flat = true, Sky = Vector3.Zero };
        scene.Taa = new PbrTaa { Enabled = true, HistoryWeight = 0f };
        for (var i = 0; i < 12; i++)
        {
            scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Point, Position = new Vector3((i % 4 - 1.5f) * 0.4f, (i / 4 - 1) * 0.4f, -1.65f),
                Range = 0.6f, Intensity = 0.4f,
            });
        }
        var temporal = pbr.Pipeline.Find<TemporalAntiAliasingFeature>()!;
        for (var phase = 0; phase < 8; phase++)
        {
            temporal.ResetHistory();
            pbr.Switches.Set(PbrFeatures.LightCulling.Id, true);
            for (var i = 0; i <= phase; i++) pbr.RenderFrame(scene);
            var binned = Capture(backend);
            temporal.ResetHistory();
            pbr.Switches.Set(PbrFeatures.LightCulling.Id, false);
            for (var i = 0; i <= phase; i++) pbr.RenderFrame(scene);
            var unbinned = Capture(backend);
            await Assert.That(binned.SequenceEqual(unbinned)).IsTrue();
            await Assert.That(binned.Where((_, i) => i % 4 == 0).Max()).IsGreaterThan((byte)20);
        }
    }

    [Test]
    public async Task Temporal_reflection_declares_compute_depth_and_storage_formats()
    {
        var program = ShaderPrograms.Load("Shaders.taa");
        var group = ShaderPrograms.FindGroup(program, 0);
        await Assert.That(program.Layout.Groups.Length).IsEqualTo(1);
        await Assert.That(group.Entries.Length).IsEqualTo(8);
        foreach (var entry in group.Entries)
            await Assert.That((entry.Visibility & ShaderStage.Compute) != 0).IsTrue();
        await Assert.That(group.Entries.Single(entry => entry.Binding == 3).Type).IsEqualTo(BindingResourceType.UnfilterableFloatTexture);
        await Assert.That(group.Entries.Single(entry => entry.Binding == 5).Type).IsEqualTo(BindingResourceType.UnfilterableFloatTexture);
        await Assert.That(group.Entries.Single(entry => entry.Binding == 6).StorageFormat).IsEqualTo(TextureFormat.Rgba16Float);
        await Assert.That(group.Entries.Single(entry => entry.Binding == 7).StorageFormat).IsEqualTo(TextureFormat.R32Float);
    }
}
