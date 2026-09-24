using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Computes ambient occlusion from hemisphere rays against the scene BVH.</summary>
/// <remarks>Requires opaque geometry and the depth/normal prepass, then publishes RayTracedAo;
/// disabled frames declare no pass.</remarks>
public sealed class RayTracedAoFeature : IRenderFeature
{
    [StructLayout(LayoutKind.Sequential, Size = 96)]
    private struct RtaoUniformsGpu
    {
        public Vector4 Params;
        public Vector4 Screen;
        public Matrix4x4 InvViewProj;
    }

    private readonly PbrContext _ctx;
    private readonly ComputePipelineHandle _pipeline;
    private readonly BindGroupLayoutDesc _group0;
    private readonly BindGroupLayoutDesc _traceGroup;
    private readonly BufferHandle _uniformBuffer;
    private uint _frame;
    private uint _targetWidth;
    private uint _targetHeight;
    private float _scale = 1f;

    internal RayTracedAoFeature(PbrContext ctx)
    {
        _ctx = ctx;
        var program = ShaderPrograms.Load("Shaders.rtao");
        _pipeline = ctx.Renderer.CreateComputePipeline(program);
        _group0 = ShaderPrograms.FindGroup(program, 0);
        _traceGroup = ShaderPrograms.FindGroup(program, 1);
        _uniformBuffer = ctx.Renderer.CreateBuffer(new BufferDesc(
            "PbrRtaoUniforms", (ulong)Unsafe.SizeOf<RtaoUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
        EnsureTarget(1f);
    }

    public FeatureDefinition Definition => PbrFeatures.RayTracedAo;
    public FrameRequirements Requires =>
        _ctx.Scene.RayTracedAo.Enabled ? FrameRequirements.DepthNormalPrepass : FrameRequirements.None;

    public void Resize(uint width, uint height) => EnsureTarget(_scale);

    private void EnsureTarget(float scale)
    {
        _scale = Math.Clamp(scale, 0.1f, 1f);
        scale = _scale;
        _targetWidth = Math.Max(1, (uint)MathF.Ceiling(_ctx.Width * scale));
        _targetHeight = Math.Max(1, (uint)MathF.Ceiling(_ctx.Height * scale));
        _ctx.Targets.Ensure(PbrTargets.RayTracedAo, new TextureDesc(
            null, _targetWidth, _targetHeight, 1, 1, 1, TextureDimension.D2, TextureFormat.Rgba16Float,
            TextureUsage.StorageBinding | TextureUsage.TextureBinding));
    }

    public void Setup(in FrameContext frame)
    {
        var settings = _ctx.Scene.RayTracedAo;
        if (!settings.Enabled) return;
        if (!frame.Blackboard.TryGet(PbrResults.PrepassNormal, out var normal)
            || !frame.Blackboard.TryGet(PbrResults.PrepassDepth, out var depth))
            return;

        EnsureTarget(settings.ResolutionScale);
        var uniforms = new RtaoUniformsGpu
        {
            Params = new Vector4(Math.Clamp(settings.RaysPerPixel, 1, 64), MathF.Max(settings.MaxDistance, 1e-3f), _frame++, settings.NormalBias),
            // xy: the AO target's size (the dispatch), zw: the pre-pass's size it reads from.
            Screen = new Vector4(_targetWidth, _targetHeight, _ctx.Width, _ctx.Height),
            InvViewProj = Matrix4x4.Invert(_ctx.ViewProjection, out var inv) ? inv : Matrix4x4.Identity,
        };
        _ctx.Renderer.UpdateBuffer<RtaoUniformsGpu>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        var output = frame.Graph.Texture(PbrTargets.RayTracedAo);
        frame.Graph.AddComputePass("Rtao.Trace", RenderPassEvent.AfterPrepass)
            .BindGroup(0, "PbrRtaoGroup", _group0,
            [
                GraphBinding.Buffer(0, _uniformBuffer, 0, (ulong)Unsafe.SizeOf<RtaoUniformsGpu>()),
                GraphBinding.Texture(1, normal),
                GraphBinding.Texture(2, depth),
                GraphBinding.StorageTexture(3, output),
            ])
            .BindGroup(1, "PbrRtaoTraceGroup", _traceGroup, _ctx.Trace.Bindings())
            .Record(this, Record);
        frame.Blackboard.Publish(PbrResults.RayTracedAo, output);
    }

    private static void Record(RayTracedAoFeature self, ref PassRecording pass, int _)
    {
        pass.Encoder.SetComputePipeline(self._pipeline);
        pass.SetBindGroup(0);
        pass.SetBindGroup(1);
        pass.Encoder.Dispatch(new DispatchCommand((self._targetWidth + 7) / 8, (self._targetHeight + 7) / 8, 1));
    }

    public void Dispose()
    {
        _ctx.Renderer.DestroyComputePipeline(_pipeline);
        _ctx.Renderer.DestroyBuffer(_uniformBuffer);
    }
}
