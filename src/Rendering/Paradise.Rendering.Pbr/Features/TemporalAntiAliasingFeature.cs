using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Accumulates jittered HDR color using camera, rigid and skinned motion vectors.</summary>
public sealed class TemporalAntiAliasingFeature : IRenderFeature
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TemporalUniformsGpu
    {
        public Vector4 Screen;
        public Vector4 Parameters;
        public Matrix4x4 PreviousInverseProjection;
    }

    private static readonly string[] s_historyColor = ["PbrTaaColor0", "PbrTaaColor1"];
    private static readonly string[] s_historyDepth = ["PbrTaaDepth0", "PbrTaaDepth1"];
    private readonly PbrContext _ctx;
    private readonly RenderPipeline _features;
    private ShaderProgramDesc? _program;
    private ComputePipelineHandle _resolvePipeline;
    private BufferHandle _uniformBuffer;
    private BindGroupLayoutDesc _layout = null!;
    private PbrScene? _scene;
    private PbrTaa? _settings;
    private ulong _version;
    private uint _sample;
    private int _writeIndex;
    private bool _historyValid;
    private bool _active;
    private bool _recorded;
    private Matrix4x4 _previousInverseProjection;

    internal TemporalAntiAliasingFeature(PbrContext ctx, RenderPipeline features)
    {
        _ctx = ctx;
        _features = features;
    }

    public FeatureDefinition Definition => PbrFeatures.TemporalAntiAliasing;
    public FrameRequirements Requires => _active ? FrameRequirements.MotionVectors : FrameRequirements.None;

    /// <summary>Whether the current resolve could read a preceding rendered frame.</summary>
    public bool HistoryReady { get; private set; }
    /// <summary>The current frame's camera offset in top-left-origin pixel units.</summary>
    public Vector2 JitterPixels { get; private set; }

    /// <summary>Discards accumulated color and restarts sampling on the next frame.</summary>
    public void ResetHistory()
    {
        _historyValid = false;
        HistoryReady = false;
        _sample = 0;
        JitterPixels = Vector2.Zero;
    }

    public void Resize(uint width, uint height) => ResetHistory();

    public void OnEnabledChanged(bool enabled)
    {
        ResetHistory();
        _active = false;
    }

    public void PrepareFrame()
    {
        var scene = _ctx.Scene;
        _active = scene.Taa.Enabled && _features.IsEnabled(PbrFeatures.MotionVectors.Id)
            && _features.IsEnabled(PbrFeatures.Scene.Id);
        if (!_active)
        {
            ResetHistory();
            return;
        }
        if (!ReferenceEquals(_scene, scene) || _version != scene.TemporalHistoryVersion || _settings != scene.Taa)
            ResetHistory();
        _scene = scene;
        _version = scene.TemporalHistoryVersion;
        _settings = scene.Taa;
        var scale = AntiAliasingMath.FiniteClamp(scene.Taa.JitterScale, 0f, 2f, 1f);
        JitterPixels = AntiAliasingMath.Jitter(_sample) * scale;
        _ctx.SetProjection(AntiAliasingMath.JitterProjection(_ctx.Projection, JitterPixels, _ctx.Width, _ctx.Height),
            JitterPixels / new Vector2(_ctx.Width, _ctx.Height));
    }

    public void Setup(in FrameContext frame)
    {
        _recorded = false;
        if (!_active || !frame.Blackboard.TryGet(PbrResults.SceneColor, out var source)
            || !frame.Blackboard.TryGet(PbrResults.MotionVectors, out var motion))
        {
            ResetHistory();
            return;
        }
        EnsureResources();
        for (var i = 0; i < 2; i++)
        {
            var usage = TextureUsage.TextureBinding | TextureUsage.StorageBinding;
            _ctx.Targets.Ensure(s_historyColor[i], _ctx.FrameTarget(TextureFormat.Rgba16Float) with { Usage = usage });
            _ctx.Targets.Ensure(s_historyDepth[i], _ctx.FrameTarget(TextureFormat.R32Float) with { Usage = usage });
        }
        var settings = _ctx.Scene.Taa;
        HistoryReady = _historyValid;
        var uniforms = new TemporalUniformsGpu
        {
            Screen = new Vector4(1f / frame.Width, 1f / frame.Height, frame.Width, frame.Height),
            Parameters = new Vector4(
                AntiAliasingMath.FiniteClamp(settings.HistoryWeight, 0f, 0.98f, 0.9f),
                AntiAliasingMath.FiniteClamp(settings.VarianceGamma, 0.5f, 3f, 1.25f),
                AntiAliasingMath.FiniteClamp(settings.DepthThreshold, 0.0001f, 0.2f, 0.02f), HistoryReady ? 1f : 0f),
            PreviousInverseProjection = _previousInverseProjection,
        };
        _ctx.Renderer.UpdateBuffer<TemporalUniformsGpu>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));
        var graph = frame.Graph;
        var output = graph.Texture(s_historyColor[_writeIndex]);
        var outputDepth = graph.Texture(s_historyDepth[_writeIndex]);
        graph.AddComputePass("Taa.Resolve", RenderPassEvent.BeforePost)
            // Both outputs are next frame's history, including when Composite is switched off.
            .NeverCull()
            .BindGroup(0, "PbrTaaResolve", _layout,
            [
                GraphBinding.Buffer(0, _uniformBuffer, 0, (ulong)Unsafe.SizeOf<TemporalUniformsGpu>()),
                GraphBinding.Texture(1, source),
                GraphBinding.Texture(2, motion),
                GraphBinding.Texture(3, graph.Texture(PbrTargets.Depth)),
                GraphBinding.Texture(4, graph.Texture(s_historyColor[1 - _writeIndex])),
                GraphBinding.Texture(5, graph.Texture(s_historyDepth[1 - _writeIndex])),
                GraphBinding.StorageTexture(6, output),
                GraphBinding.StorageTexture(7, outputDepth),
            ])
            .Record(this, Record);
        frame.Blackboard.Advance(PbrResults.SceneColor, source, output);
    }

    private void EnsureResources()
    {
        if (_program is not null) return;
        _program = ShaderPrograms.Load("Shaders.taa");
        UniformLayoutValidator.ValidateBlock(_program, "temporal", (uint)Unsafe.SizeOf<TemporalUniformsGpu>(),
            [("screen", 0, 16), ("parameters", 16, 16), ("previousInverseProjection", 32, 64)]);
        _layout = ShaderPrograms.FindGroup(_program, 0);
        _resolvePipeline = _ctx.Renderer.CreateComputePipeline(_program);
        _uniformBuffer = _ctx.Renderer.CreateBuffer(new BufferDesc("PbrTaaUniforms",
            (ulong)Unsafe.SizeOf<TemporalUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
    }

    private static void Record(TemporalAntiAliasingFeature self, ref PassRecording pass, int _)
    {
        pass.Encoder.SetComputePipeline(self._resolvePipeline);
        pass.SetBindGroup(0);
        pass.Encoder.Dispatch(new DispatchCommand((self._ctx.Width + 7) / 8, (self._ctx.Height + 7) / 8, 1));
        self._recorded = true;
    }

    public void BeforeSubmit()
    {
        if (!_recorded) return;
        _previousInverseProjection = Matrix4x4.Invert(_ctx.Projection, out var inverse) ? inverse : Matrix4x4.Identity;
        _historyValid = true;
        _sample = (_sample + 1) % AntiAliasingMath.SampleCount;
        _writeIndex = 1 - _writeIndex;
    }

    public void Dispose()
    {
        if (_program is null) return;
        _ctx.Renderer.DestroyComputePipeline(_resolvePipeline);
        _ctx.Renderer.DestroyBuffer(_uniformBuffer);
    }
}
