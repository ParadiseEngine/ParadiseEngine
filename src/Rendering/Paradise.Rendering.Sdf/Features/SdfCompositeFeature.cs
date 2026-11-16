using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Sdf;

/// <summary>Adds bloom to the current HDR scene and tonemaps it into the backbuffer, or, when a
/// later effect asked for <see cref="FrameRequirements.DisplayColor"/>, into
/// <see cref="SdfTargets.DisplayColor"/> for that effect to present.</summary>
public sealed class SdfCompositeFeature : IRenderFeature
{
    private readonly SdfContext _ctx;
    private readonly PipelineHandle _pipeline;
    private readonly PipelineHandle _displayPipeline;
    private readonly BufferHandle _uniformBuffer;
    private readonly BindGroupLayoutDesc _layout;

    internal SdfCompositeFeature(SdfContext ctx)
    {
        _ctx = ctx;
        var renderer = ctx.Renderer;
        var program = SdfContext.LoadProgram("Shaders.sdfComposite");
        SdfUniformLayout.ValidateComposite(program);
        _layout = SdfContext.FindGroup(program, 0);
        // sRGB formats let the hardware encode (linear entry); everything else encodes in-shader.
        var hardwareSrgb = renderer.ColorFormat is TextureFormat.Rgba8UnormSrgb or TextureFormat.Bgra8UnormSrgb;
        _pipeline = renderer.CreatePipeline(program, renderer.ColorFormat,
            fragmentEntryPoint: hardwareSrgb ? "compositeFragment" : "compositeFragmentSrgb");
        // Display-linear: whoever presents it encodes.
        _displayPipeline = renderer.CreatePipeline(program, SdfTargets.HdrFormat, fragmentEntryPoint: "compositeFragment");
        _uniformBuffer = renderer.CreateBuffer(new BufferDesc(
            "SdfCompositeUniforms", UniformBytes, BufferUsage.Uniform | BufferUsage.CopyDst));
    }

    private static ulong UniformBytes => (ulong)Unsafe.SizeOf<SdfCompositeUniformsGpu>();

    public FeatureDefinition Definition => SdfFeatures.Composite;
    public FrameRequirements Requires => FrameRequirements.None;

    public void Resize(uint width, uint height)
    {
    }

    public void Setup(in FrameContext frame)
    {
        var tonemap = _ctx.Scene.Tonemap;
        var bloomSettings = _ctx.Scene.Bloom;
        // Bloom is switched off in one place: with nothing published the binding is black.
        var hasBloom = frame.Blackboard.TryGet(SdfResults.Bloom, out var bloom);
        var uniforms = new SdfCompositeUniformsGpu
        {
            Tone = new Vector4(
                (float)tonemap.Mode,
                float.IsFinite(tonemap.Exposure) ? MathF.Max(tonemap.Exposure, 0f) : 1f,
                hasBloom && float.IsFinite(bloomSettings.Intensity) ? MathF.Max(bloomSettings.Intensity, 0f) : 0f,
                0f),
        };
        _ctx.Renderer.UpdateBuffer<SdfCompositeUniformsGpu>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        var graph = frame.Graph;
        var intermediate = (frame.Requirements & FrameRequirements.DisplayColor) != 0;
        var output = FrameGraph.Backbuffer;
        if (intermediate)
        {
            _ctx.Targets.Ensure(SdfTargets.DisplayColor, _ctx.FrameTarget(SdfTargets.HdrFormat));
            output = graph.Texture(SdfTargets.DisplayColor);
            frame.Blackboard.Publish(SdfResults.DisplayColor, output);
        }

        // With the scene switched off nothing is published and the frame presents black.
        graph.AddRasterPass("Sdf.Composite", RenderPassEvent.Composite)
            .Color(0, output, LoadOp.Clear, clear: new ColorRgba(0f, 0f, 0f, 1f))
            .BindGroup(0, "SdfComposite", _layout,
            [
                GraphBinding.Texture(0, frame.Blackboard.GetOrDefault(SdfResults.SceneColor, frame.Black)),
                GraphBinding.Sampler(1, _ctx.LinearClampSampler),
                GraphBinding.Buffer(2, _uniformBuffer, 0, UniformBytes),
                GraphBinding.Texture(3, hasBloom ? bloom : frame.Black),
            ])
            .Record(this, Record, intermediate ? 1 : 0);
    }

    private static void Record(SdfCompositeFeature self, ref PassRecording pass, int intermediate) =>
        SdfContext.DrawFullscreen(ref pass, intermediate != 0 ? self._displayPipeline : self._pipeline);

    public void Dispose()
    {
        _ctx.Renderer.DestroyPipeline(_pipeline);
        _ctx.Renderer.DestroyPipeline(_displayPipeline);
        _ctx.Renderer.DestroyBuffer(_uniformBuffer);
    }
}
