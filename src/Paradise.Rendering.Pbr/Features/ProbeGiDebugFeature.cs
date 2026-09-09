using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Rendering.Graph;
using Paradise.Features;

namespace Paradise.Rendering.Pbr;

/// <summary>Draws depth-tested markers for the current relocated GI probes.</summary>
public sealed class ProbeGiDebugFeature : IRenderFeature
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DebugUniforms
    {
        public Matrix4x4 ViewProjection;
        public Vector4 Right;
        public Vector4 Up;
    }

    private PipelineHandle _debugPipeline;
    private BufferHandle _debugUniformBuffer;
    private ShaderProgramDesc? _debugProgram;

    private readonly PbrContext _ctx;
    private readonly ProbeGiFeature _gi;

    internal ProbeGiDebugFeature(PbrContext ctx, ProbeGiFeature gi)
    {
        _ctx = ctx;
        _gi = gi;
    }

    public FeatureDefinition Definition => PbrFeatures.GiProbes;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>World-space radius of probe markers in metres.</summary>
    public float ProbeRadius { get; set; } = 0.08f;

    public void Resize(uint width, uint height) { }

    public void Setup(in FrameContext frame)
    {
        if (_gi.ProbeCount == 0
            || !frame.Blackboard.TryGet(PbrResults.GiIrradiance, out var irradiance)
            || !frame.Blackboard.TryGet(PbrResults.GiVisibility, out var visibility)) return;
        if (!_debugPipeline.IsValid)
        {
            _debugProgram = ShaderPrograms.Load("Shaders.probeDebug");
            _debugPipeline = _ctx.Renderer.CreatePipeline(_debugProgram, PbrTargets.HdrFormat,
                depthStencilFormat: TextureFormat.Depth32Float,
                depthWriteEnabled: false, depthCompare: CompareFunction.LessEqual);
            _debugUniformBuffer = _ctx.Renderer.CreateBuffer(new BufferDesc(
                "PbrProbeDebug", 96, BufferUsage.Uniform | BufferUsage.CopyDst));
        }

        Matrix4x4.Invert(_ctx.View, out var camera);
        var radius = Math.Clamp(ProbeRadius, 0.001f, 10f);
        var uniforms = new DebugUniforms
        {
            ViewProjection = _ctx.ViewProjection,
            Right = new Vector4(camera.M11, camera.M12, camera.M13, radius),
            Up = new Vector4(camera.M21, camera.M22, camera.M23, 0f),
        };
        _ctx.Renderer.UpdateBuffer<DebugUniforms>(_debugUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));
        var graph = frame.Graph;
        graph.AddRasterPass("Gi.DebugProbes", RenderPassEvent.AfterTransparent)
            .Color(0, graph.Texture(PbrTargets.Hdr), LoadOp.Load)
            .Depth(graph.Texture(PbrTargets.Depth), LoadOp.Load)
            .BindGroup(0, "PbrProbeDebugCamera", ShaderPrograms.FindGroup(_debugProgram!, 0),
                [GraphBinding.Buffer(0, _debugUniformBuffer, 0, 96)])
            .BindGroup(3, "PbrProbeDebugStates", ShaderPrograms.FindGroup(_debugProgram!, 3),
                _gi.ProbeGroupBindings(_debugProgram!, irradiance, visibility, _gi.ShadingStateBuffer))
            .Record(this, RecordDebug, _gi.ProbeCount);
    }

    private static void RecordDebug(ProbeGiDebugFeature self, ref PassRecording pass, int count)
    {
        pass.Encoder.SetPipeline(self._debugPipeline);
        pass.SetBindGroup(0);
        pass.SetBindGroup(3);
        pass.Encoder.Draw(new DrawCommand(6, (uint)count, 0, 0));
    }

    public void Dispose()
    {
        if (_debugPipeline.IsValid) _ctx.Renderer.DestroyPipeline(_debugPipeline);
        if (_debugUniformBuffer.IsValid) _ctx.Renderer.DestroyBuffer(_debugUniformBuffer);
    }
}
