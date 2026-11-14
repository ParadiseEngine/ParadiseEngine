using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Sdf;

/// <summary>Tonemaps the current HDR scene into the backbuffer.</summary>
public sealed class SdfCompositeFeature : IRenderFeature
{
    private readonly SdfContext _ctx;
    private readonly PipelineHandle _pipeline;
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
        var uniforms = new SdfCompositeUniformsGpu
        {
            Tone = new Vector4((float)tonemap.Mode, float.IsFinite(tonemap.Exposure) ? MathF.Max(tonemap.Exposure, 0f) : 1f, 0f, 0f),
        };
        _ctx.Renderer.UpdateBuffer<SdfCompositeUniformsGpu>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        // With the scene switched off nothing is published and the frame presents black.
        frame.Graph.AddRasterPass("Sdf.Composite", RenderPassEvent.Composite)
            .Color(0, FrameGraph.Backbuffer, LoadOp.Clear, clear: new ColorRgba(0f, 0f, 0f, 1f))
            .BindGroup(0, "SdfComposite", _layout,
            [
                GraphBinding.Texture(0, frame.Blackboard.GetOrDefault(SdfResults.SceneColor, frame.Black)),
                GraphBinding.Sampler(1, _ctx.LinearClampSampler),
                GraphBinding.Buffer(2, _uniformBuffer, 0, UniformBytes),
            ])
            .Record(this, Record);
    }

    private static void Record(SdfCompositeFeature self, ref PassRecording pass, int _)
    {
        pass.Encoder.SetPipeline(self._pipeline);
        pass.SetBindGroup(0);
        pass.Encoder.Draw(new DrawCommand(3, 1, 0, 0));
    }

    public void Dispose()
    {
        _ctx.Renderer.DestroyPipeline(_pipeline);
        _ctx.Renderer.DestroyBuffer(_uniformBuffer);
    }
}
