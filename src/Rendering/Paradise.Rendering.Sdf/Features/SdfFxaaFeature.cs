using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Sdf;

/// <summary>Smooths high-contrast edges of the tonemapped frame and presents it.</summary>
/// <remarks>While it is on it asks for <see cref="FrameRequirements.DisplayColor"/>: the
/// composite then tonemaps into <see cref="SdfTargets.DisplayColor"/> instead of the backbuffer,
/// and this feature's pass is the one that reaches the backbuffer.</remarks>
public sealed class SdfFxaaFeature : IRenderFeature
{
    private readonly SdfContext _ctx;
    private readonly PipelineHandle _pipeline;
    private readonly BufferHandle _uniformBuffer;
    private readonly BindGroupLayoutDesc _layout;

    internal SdfFxaaFeature(SdfContext ctx)
    {
        _ctx = ctx;
        var renderer = ctx.Renderer;
        var program = SdfContext.LoadProgram("Shaders.sdfFxaa");
        SdfUniformLayout.ValidateFxaa(program);
        _layout = SdfContext.FindGroup(program, 0);
        // sRGB formats let the hardware encode (linear entry); everything else encodes in-shader.
        var hardwareSrgb = renderer.ColorFormat is TextureFormat.Rgba8UnormSrgb or TextureFormat.Bgra8UnormSrgb;
        _pipeline = renderer.CreatePipeline(program, renderer.ColorFormat,
            fragmentEntryPoint: hardwareSrgb ? "fxaaFragment" : "fxaaFragmentSrgb");
        _uniformBuffer = renderer.CreateBuffer(new BufferDesc("SdfFxaaUniforms", UniformBytes, BufferUsage.Uniform | BufferUsage.CopyDst));
    }

    private static ulong UniformBytes => (ulong)Unsafe.SizeOf<SdfFxaaUniformsGpu>();

    public FeatureDefinition Definition => SdfFeatures.Fxaa;
    public FrameRequirements Requires => _ctx.Scene.Fxaa.Enabled ? FrameRequirements.DisplayColor : FrameRequirements.None;

    public void Resize(uint width, uint height)
    {
    }

    public void Setup(in FrameContext frame)
    {
        var settings = _ctx.Scene.Fxaa;
        // With the composite switched off nothing is published and the frame is never presented.
        if (!settings.Enabled || !frame.Blackboard.TryGet(SdfResults.DisplayColor, out var source)) return;

        var uniforms = new SdfFxaaUniformsGpu
        {
            Screen = new Vector4(1f / frame.Width, 1f / frame.Height, frame.Width, frame.Height),
            Parameters = new Vector4(
                FiniteClamp(settings.EdgeThreshold, 0.0312f, 0.5f, 0.125f),
                FiniteClamp(settings.MinimumThreshold, 0f, 0.25f, 0.0312f),
                FiniteClamp(settings.SubpixelQuality, 0f, 1f, 0.75f), 0f),
        };
        _ctx.Renderer.UpdateBuffer<SdfFxaaUniformsGpu>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        frame.Graph.AddRasterPass("Sdf.Fxaa", RenderPassEvent.Composite, offset: 60)
            .Color(0, FrameGraph.Backbuffer, LoadOp.Clear, clear: new ColorRgba(0f, 0f, 0f, 1f))
            .BindGroup(0, "SdfFxaa", _layout,
            [
                GraphBinding.Texture(0, source),
                GraphBinding.Sampler(1, _ctx.LinearClampSampler),
                GraphBinding.Buffer(2, _uniformBuffer, 0, UniformBytes),
            ])
            .Record(this, Record);
    }

    private static float FiniteClamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static void Record(SdfFxaaFeature self, ref PassRecording pass, int _) =>
        SdfContext.DrawFullscreen(ref pass, self._pipeline);

    public void Dispose()
    {
        _ctx.Renderer.DestroyPipeline(_pipeline);
        _ctx.Renderer.DestroyBuffer(_uniformBuffer);
    }
}
