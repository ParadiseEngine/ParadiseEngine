using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Resolves high-contrast edges in display-linear color before final presentation.</summary>
public sealed class FxaaFeature : IRenderFeature
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FxaaUniformsGpu
    {
        public Vector4 Screen;
        public Vector4 Parameters;
    }

    private const string Target = "PbrFxaaColor";
    private readonly PbrContext _ctx;
    private ShaderProgramDesc? _program;
    private PipelineHandle _pipeline;
    private BufferHandle _uniformBuffer;
    private BindGroupLayoutDesc _layout = null!;

    internal FxaaFeature(PbrContext ctx) => _ctx = ctx;

    public FeatureDefinition Definition => PbrFeatures.Fxaa;
    public FrameRequirements Requires => _ctx.Scene.Fxaa.Enabled ? FrameRequirements.DisplayColor : FrameRequirements.None;
    public void Resize(uint width, uint height) { }

    public void Setup(in FrameContext frame)
    {
        var settings = _ctx.Scene.Fxaa;
        if (!settings.Enabled || !frame.Blackboard.TryGet(PbrResults.DisplayColor, out var source)) return;
        EnsureResources();
        _ctx.Targets.Ensure(Target, _ctx.FrameTarget(TextureFormat.Rgba16Float));
        var uniforms = new FxaaUniformsGpu
        {
            Screen = new Vector4(1f / frame.Width, 1f / frame.Height, frame.Width, frame.Height),
            Parameters = new Vector4(AntiAliasingMath.FiniteClamp(settings.EdgeThreshold, 0.0312f, 0.5f, 0.125f),
                AntiAliasingMath.FiniteClamp(settings.MinimumThreshold, 0f, 0.25f, 0.0312f),
                AntiAliasingMath.FiniteClamp(settings.SubpixelQuality, 0f, 1f, 0.75f), 0f),
        };
        _ctx.Renderer.UpdateBuffer<FxaaUniformsGpu>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));
        var output = frame.Graph.Texture(Target);
        frame.Graph.AddRasterPass("Fxaa.Resolve", RenderPassEvent.Composite, offset: 60)
            .Color(0, output, LoadOp.Clear)
            .BindGroup(0, "PbrFxaa", _layout,
            [
                GraphBinding.Texture(0, source),
                GraphBinding.Sampler(1, _ctx.LinearClampSampler),
                GraphBinding.Buffer(2, _uniformBuffer, 0, (ulong)Unsafe.SizeOf<FxaaUniformsGpu>()),
            ])
            .Record(this, Record);
        frame.Blackboard.Advance(PbrResults.DisplayColor, source, output);
    }

    private void EnsureResources()
    {
        if (_program is not null) return;
        _program = ShaderPrograms.Load("Shaders.fxaa");
        UniformLayoutValidator.ValidateBlock(_program, "fxaa", (uint)Unsafe.SizeOf<FxaaUniformsGpu>(),
            [("screen", 0, 16), ("parameters", 16, 16)]);
        _layout = ShaderPrograms.FindGroup(_program, 0);
        _pipeline = _ctx.Renderer.CreatePipeline(_program, TextureFormat.Rgba16Float);
        _uniformBuffer = _ctx.Renderer.CreateBuffer(new BufferDesc("PbrFxaaUniforms", (ulong)Unsafe.SizeOf<FxaaUniformsGpu>(),
            BufferUsage.Uniform | BufferUsage.CopyDst));
    }

    private static void Record(FxaaFeature self, ref PassRecording pass, int _) => Fullscreen.Record(ref pass, self._pipeline);

    public void Dispose()
    {
        if (_program is null) return;
        _ctx.Renderer.DestroyPipeline(_pipeline);
        _ctx.Renderer.DestroyBuffer(_uniformBuffer);
    }
}
