using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;
using static Paradise.Rendering.Pbr.PbrPostMath;

namespace Paradise.Rendering.Pbr;

/// <summary>GPU log-luminance metering, bounded temporal EV adaptation and HDR exposure.</summary>
public sealed class ExposureFeature : IRenderFeature
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ExposureUniformsGpu
    {
        public Vector4 Limits;
        public Vector4 Adaptation;
        public Vector4 Mode;
    }

    private const string Output = "PbrExposedColor";
    private static readonly string[] s_history = ["PbrExposure0", "PbrExposure1"];
    private readonly PbrContext _ctx;
    private readonly PipelineHandle _logPipeline;
    private readonly PipelineHandle _reducePipeline;
    private readonly PipelineHandle _adaptPipeline;
    private readonly PipelineHandle _applyPipeline;
    private readonly BindGroupLayoutDesc _layout;
    private readonly BufferHandle _uniforms;
    private readonly List<string> _levels = [];
    private bool _historyValid;
    private int _ping;
    private PbrScene? _previousScene;
    private ulong _version;
    private bool _automatic;

    internal ExposureFeature(PbrContext ctx)
    {
        _ctx = ctx;
        var program = ShaderPrograms.Load("Shaders.exposure");
        _layout = ShaderPrograms.FindGroup(program, 0);
        _logPipeline = ctx.Renderer.CreatePipeline(program, PbrTargets.HdrFormat, fragmentEntryPoint: "logLuminanceFragment");
        _reducePipeline = ctx.Renderer.CreatePipeline(program, PbrTargets.HdrFormat, fragmentEntryPoint: "reduceLuminanceFragment");
        _adaptPipeline = ctx.Renderer.CreatePipeline(program, PbrTargets.HdrFormat, fragmentEntryPoint: "adaptExposureFragment");
        _applyPipeline = ctx.Renderer.CreatePipeline(program, PbrTargets.HdrFormat, fragmentEntryPoint: "applyExposureFragment");
        _uniforms = ctx.Renderer.CreateBuffer(new BufferDesc("PbrExposureUniforms",
            (ulong)Unsafe.SizeOf<ExposureUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
    }

    public FeatureDefinition Definition => PbrFeatures.Exposure;
    public FrameRequirements Requires => FrameRequirements.None;
    public void Resize(uint width, uint height) => ResetHistory();
    public void ResetHistory() => _historyValid = false;

    public void OnEnabledChanged(bool enabled)
    {
        if (!enabled) Release();
    }

    private void EnsureTargets(bool automatic)
    {
        _ctx.Targets.Ensure(Output, _ctx.FrameTarget(PbrTargets.HdrFormat));
        foreach (var name in s_history) _ctx.Targets.Ensure(name, PbrTargets.RenderTarget(1, 1, PbrTargets.HdrFormat));
        var count = 0;
        if (automatic)
        {
            var width = _ctx.Width;
            var height = _ctx.Height;
            do
            {
                width = Math.Max(1, (width + 3) / 4);
                height = Math.Max(1, (height + 3) / 4);
                if (count == _levels.Count) _levels.Add("PbrExposureMeter" + count);
                _ctx.Targets.Ensure(_levels[count++], PbrTargets.RenderTarget(width, height, PbrTargets.HdrFormat));
            } while (width > 1 || height > 1);
        }
        for (var i = count; i < _levels.Count; i++) _ctx.Targets.Release(_levels[i]);
        if (count < _levels.Count) _levels.RemoveRange(count, _levels.Count - count);
    }

    public void Setup(in FrameContext frame)
    {
        var scene = _ctx.Scene;
        var settings = scene.Exposure;
        if (!settings.Enabled)
        {
            Release();
            return;
        }
        if (!frame.Blackboard.TryGet(PbrResults.SceneColor, out var source)) return;
        if (!ReferenceEquals(scene, _previousScene) || scene.TemporalHistoryVersion != _version || settings.Automatic != _automatic)
            ResetHistory();
        EnsureTargets(settings.Automatic);
        var minimum = Finite(settings.MinEv, -10f, -24f, 24f);
        var maximum = Finite(settings.MaxEv, 10f, minimum, 24f);
        var uniforms = new ExposureUniformsGpu
        {
            Limits = new Vector4(minimum, maximum, Finite(settings.CompensationEv, 0f, -24f, 24f),
                Finite(settings.MiddleGray, 0.18f, 0.001f, 1f)),
            Adaptation = new Vector4(Finite(settings.BrightenSpeed, 2f, 0f, 100f), Finite(settings.DarkenSpeed, 4f, 0f, 100f),
                Finite(scene.DeltaSeconds, 0f, 0f, 1f), _historyValid && settings.Automatic ? 1f : 0f),
            Mode = new Vector4(settings.Automatic ? 1f : 0f, 0f, 0f, 0f),
        };
        _ctx.Renderer.UpdateBuffer<ExposureUniformsGpu>(_uniforms, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));
        var meter = source;
        for (var i = 0; i < _levels.Count; i++)
        {
            var target = frame.Graph.Texture(_levels[i]);
            Declare(frame, i == 0 ? "Exposure.LogLuminance" : "Exposure.Reduce", target, meter, frame.Black)
                .Record(this, i == 0 ? RecordLog : RecordReduce);
            meter = target;
        }
        var exposure = frame.Graph.Texture(s_history[_ping]);
        var previous = frame.Graph.Texture(s_history[1 - _ping]);
        Declare(frame, "Exposure.Adapt", exposure, meter, previous)
            .NeverCull()
            .Record(this, RecordAdapt);
        var output = frame.Graph.Texture(Output);
        Declare(frame, "Exposure.Apply", output, source, exposure).Record(this, RecordApply);
        frame.Blackboard.Advance(PbrResults.SceneColor, source, output);
        _historyValid = true;
        _previousScene = scene;
        _version = scene.TemporalHistoryVersion;
        _automatic = settings.Automatic;
        _ping = 1 - _ping;
    }

    private FrameGraph.PassBuilder Declare(in FrameContext frame, string name, GraphTexture target, GraphTexture source, GraphTexture exposure) =>
        frame.Graph.AddRasterPass(name, RenderPassEvent.BeforePost, 10)
            .Color(0, target, LoadOp.Clear, StoreOp.Store)
            .BindGroup(0, "PbrExposure", _layout,
            [
                GraphBinding.Texture(0, source),
                GraphBinding.Sampler(1, _ctx.LinearClampSampler),
                GraphBinding.Buffer(2, _uniforms, 0, (ulong)Unsafe.SizeOf<ExposureUniformsGpu>()),
                GraphBinding.Texture(3, exposure),
            ]);

    private static void RecordLog(ExposureFeature self, ref PassRecording pass, int _) => Fullscreen.Record(ref pass, self._logPipeline);
    private static void RecordReduce(ExposureFeature self, ref PassRecording pass, int _) => Fullscreen.Record(ref pass, self._reducePipeline);
    private static void RecordAdapt(ExposureFeature self, ref PassRecording pass, int _) => Fullscreen.Record(ref pass, self._adaptPipeline);
    private static void RecordApply(ExposureFeature self, ref PassRecording pass, int _) => Fullscreen.Record(ref pass, self._applyPipeline);

    private void Release()
    {
        ResetHistory();
        _previousScene = null;
        _ctx.Targets.Release(Output);
        foreach (var name in s_history) _ctx.Targets.Release(name);
        foreach (var name in _levels) _ctx.Targets.Release(name);
        _levels.Clear();
    }

    public void Dispose()
    {
        Release();
        _ctx.Renderer.DestroyPipeline(_logPipeline);
        _ctx.Renderer.DestroyPipeline(_reducePipeline);
        _ctx.Renderer.DestroyPipeline(_adaptPipeline);
        _ctx.Renderer.DestroyPipeline(_applyPipeline);
        _ctx.Renderer.DestroyBuffer(_uniforms);
    }
}
