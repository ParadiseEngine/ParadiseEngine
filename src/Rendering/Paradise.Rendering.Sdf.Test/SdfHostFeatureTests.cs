using System.Runtime.InteropServices;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Sdf.Test;

/// <summary>The seam from a host's side: a feature the renderer did not construct rasterizes
/// into the traced frame and is depth-tested against the traced surface.</summary>
/// <remarks>Not in parallel, like <see cref="SdfRendererGpuTests"/>.</remarks>
[NotInParallel]
public class SdfHostFeatureTests
{
    private const uint Size = 96;

    /// <summary>Draws a white plane at one device depth over the HDR scene, behind whatever the
    /// trace wrote nearer.</summary>
    private sealed class DepthPlaneFeature : IRenderFeature
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PlaneUniforms
        {
            public Vector4 Color;
            public Vector4 Depth;
        }

        private readonly IRenderer _renderer;
        private readonly PipelineHandle _pipeline;
        private readonly BufferHandle _buffer;
        private readonly BindGroupLayoutDesc _layout;

        public DepthPlaneFeature(IRenderer renderer)
        {
            _renderer = renderer;
            var program = ShaderProgramLoader.Load(typeof(SdfHostFeatureTests).Assembly, "Shaders.depthPlaneFixture");
            _layout = program.Layout.Groups[0];
            _pipeline = renderer.CreatePipeline(program, SdfTargets.HdrFormat,
                depthStencilFormat: SdfTargets.DepthFormat, depthWriteEnabled: false, depthCompare: CompareFunction.LessEqual);
            _buffer = renderer.CreateBuffer(new BufferDesc("HostDepthPlane", 32, BufferUsage.Uniform | BufferUsage.CopyDst));
        }

        public float Depth;
        public int Recorded;

        public FeatureDefinition Definition { get; } = new("game.sdfDepthPlane", true, "A plane composed by depth with the SDF scene.");
        public FrameRequirements Requires => FrameRequirements.None;

        public void Resize(uint width, uint height)
        {
        }

        public void Setup(in FrameContext frame)
        {
            if (!frame.Blackboard.TryGet(SdfResults.SceneColor, out var color)) return;
            var uniforms = new PlaneUniforms { Color = Vector4.One, Depth = new Vector4(Depth, 0f, 0f, 0f) };
            _renderer.UpdateBuffer<PlaneUniforms>(_buffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));
            frame.Graph.AddRasterPass("Host.DepthPlane", RenderPassEvent.AfterOpaque)
                .Color(0, color, LoadOp.Load)
                .Depth(frame.Graph.Texture(SdfTargets.Depth), LoadOp.Load)
                .BindGroup(0, "HostDepthPlane", _layout, [GraphBinding.Buffer(0, _buffer, 0, 32)])
                .Record(this, Record);
        }

        private static void Record(DepthPlaneFeature self, ref PassRecording pass, int _)
        {
            self.Recorded++;
            pass.Encoder.SetPipeline(self._pipeline);
            pass.SetBindGroup(0);
            pass.Encoder.Draw(new DrawCommand(3, 1, 0, 0));
        }

        public void Dispose()
        {
            _renderer.DestroyPipeline(_pipeline);
            _renderer.DestroyBuffer(_buffer);
        }
    }

    /// <summary>Whether a LessEqual plane at <paramref name="planeDepth"/> covers the traced
    /// scene through a screen point, by the CPU field.</summary>
    /// <param name="clear">False where the surface is within a hit threshold of the plane.</param>
    private static bool PlaneShows(SdfField field, SdfScene scene, float planeDepth, Vector2 screen, out bool clear)
    {
        var sceneDepth = TestFrame.TryCpuHit(field, scene, Size, screen, out var point) ? TestFrame.DeviceDepth(scene, point) : 1f;
        clear = MathF.Abs(sceneDepth - planeDepth) >= 1.5e-3f;
        return planeDepth <= sceneDepth;
    }

    [Test]
    public async Task a_host_raster_pass_is_depth_tested_against_the_traced_surface()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        using var renderer = new SdfRenderer(backend, new FeatureSwitches(), Size, Size);
        var plane = new DepthPlaneFeature(backend);
        renderer.Pipeline.Add(plane, SdfFeatureOrder.Scene + 1);
        var scene = TestFrame.ParityScene();
        // A view-facing plane through the middle of the shapes: part of the scene is in front.
        var eye = new Vector3(2.5f, 2.2f, 4.5f);
        var forward = Vector3.Normalize(new Vector3(0f, 0.6f, 0f) - eye);
        plane.Depth = TestFrame.DeviceDepth(scene, eye + forward * 5.4f);

        var pixels = TestFrame.Render(backend, renderer, scene);

        var field = new SdfField();
        field.Set(CollectionsMarshal.AsSpan(scene.Shapes));
        var inFront = 0;
        var behind = 0;
        var mismatches = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                // Judged only where the answer is the same across the pixel's interior and the
                // surface is clear of the plane: elsewhere a hit one threshold apart decides either way.
                var center = TestFrame.PixelCenter(x, y);
                var expectPlane = PlaneShows(field, scene, plane.Depth, center, out var decided);
                foreach (var offset in TestFrame.Interior)
                    decided &= PlaneShows(field, scene, plane.Depth, center + offset, out var clear) == expectPlane && clear;
                if (!decided) continue;
                if (expectPlane) behind++;
                else inFront++;
                var (r, g, b) = TestFrame.Pixel(pixels, Size, x, y);
                var isPlane = r > 250 && g > 250 && b > 250;
                if (isPlane != expectPlane) mismatches++;
            }
        }

        var total = (int)(Size * Size);
        await Assert.That(inFront).IsGreaterThan(total / 4);
        await Assert.That(behind).IsGreaterThan(total / 4);
        await Assert.That(mismatches).IsEqualTo(0);
        await Assert.That(plane.Recorded).IsEqualTo(1);
        await Assert.That(renderer.LastPassNames.SequenceEqual(["Sdf.Scene", "Host.DepthPlane", "Sdf.Composite"])).IsTrue();
    }

    [Test]
    public async Task a_host_feature_follows_the_scene_switch_through_the_blackboard()
    {
        using var backend = TestFrame.Backend(Size);
        if (backend is null) return;
        var switches = new FeatureSwitches();
        using var renderer = new SdfRenderer(backend, switches, Size, Size);
        var plane = new DepthPlaneFeature(backend) { Depth = 0.5f };
        renderer.Pipeline.Add(plane);
        var scene = TestFrame.ParityScene();
        switches.Set(SdfFeatures.Scene.Id, false);

        renderer.RenderFrame(scene);

        // Nothing published, nothing to compose with: the host pass is never declared.
        await Assert.That(plane.Recorded).IsEqualTo(0);
        await Assert.That(renderer.LastPassNames.SequenceEqual(["Sdf.Composite"])).IsTrue();
        await Assert.That(switches.IsEnabled(plane.Definition.Id)).IsTrue();
    }
}
