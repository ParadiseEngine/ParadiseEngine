using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Current-frame conservative GPU occlusion using max-depth tiles and indexed indirect draws.</summary>
public sealed class OcclusionCullingFeature : IRenderFeature
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DrawBoundsGpu
    {
        public Vector4 Rectangle;
        public float Nearest;
        public uint IndexCount;
        public uint Visible;
        public uint Projected;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OcclusionUniformsGpu
    {
        public Vector4 Screen;
        public Vector4 Counts;
    }

    public const uint IndirectStride = 5 * sizeof(uint);
    private readonly PbrContext _ctx;
    private readonly FrustumCullingFeature _frustum;
    private readonly PipelineHandle _depthPipeline;
    private readonly ComputePipelineHandle _reducePipeline;
    private readonly ComputePipelineHandle _cullPipeline;
    private readonly BindGroupLayoutDesc _reduceGroup;
    private readonly BindGroupLayoutDesc _cullGroup;
    private readonly BufferHandle _uniforms;
    private readonly BufferHandle _boundsBuffer;
    private readonly DrawBoundsGpu[] _bounds = new DrawBoundsGpu[PbrContext.MaxDrawsPerFrame];
    private BufferHandle _depthTiles;
    private int _tilesX;
    private int _tilesY;
    private GraphBuffer _graphArguments;

    internal OcclusionCullingFeature(PbrContext ctx, FrustumCullingFeature frustum)
    {
        _ctx = ctx;
        _frustum = frustum;
        var depth = ShaderPrograms.WithDynamicDrawRing(ShaderPrograms.Load("Shaders.occluderDepth"));
        _depthPipeline = ctx.Renderer.CreateDepthOnlyPipeline(depth, TextureFormat.Depth32Float, depth.VertexBuffers);
        var reduce = ShaderPrograms.Load("Shaders.occlusionDepth");
        _reducePipeline = ctx.Renderer.CreateComputePipeline(reduce);
        _reduceGroup = ShaderPrograms.FindGroup(reduce, 0);
        var cull = ShaderPrograms.Load("Shaders.occlusionCull");
        _cullPipeline = ctx.Renderer.CreateComputePipeline(cull);
        _cullGroup = ShaderPrograms.FindGroup(cull, 0);
        _uniforms = ctx.Renderer.CreateBuffer(new BufferDesc("PbrOcclusionUniforms", 32, BufferUsage.Uniform | BufferUsage.CopyDst));
        _boundsBuffer = ctx.Renderer.CreateBuffer(new BufferDesc("PbrOcclusionBounds", BoundsBytes, BufferUsage.Storage | BufferUsage.CopyDst));
        IndirectBuffer = ctx.Renderer.CreateBuffer(new BufferDesc("PbrOcclusionArguments", IndirectBufferBytes,
            BufferUsage.Storage | BufferUsage.Indirect | BufferUsage.CopySrc));
        EnsureTargets();
    }

    public FeatureDefinition Definition => PbrFeatures.OcclusionCulling;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>True only when this frame generates the indirect arguments.</summary>
    public bool Active { get; private set; }

    /// <summary>One indexed-indirect record per opaque draw: index count, instance count, first index, signed base vertex and first instance.</summary>
    public BufferHandle IndirectBuffer { get; }
    public ulong IndirectBufferBytes => PbrContext.MaxDrawsPerFrame * IndirectStride;
    public int DrawCount { get; private set; }
    private static ulong BoundsBytes => (ulong)(PbrContext.MaxDrawsPerFrame * Unsafe.SizeOf<DrawBoundsGpu>());
    internal ReadOnlySpan<DrawBoundsGpu> Bounds => _bounds.AsSpan(0, DrawCount);
    internal BufferHandle DepthTiles => _depthTiles;
    internal int TilesX => _tilesX;
    internal ulong DepthTileBytes => (ulong)(_tilesX * _tilesY * sizeof(float));

    public void OnEnabledChanged(bool enabled)
    {
        Active = false;
        DrawCount = 0;
    }

    public void Resize(uint width, uint height) => EnsureTargets();

    private void EnsureTargets()
    {
        _ctx.Targets.Ensure(PbrTargets.OccluderDepth, _ctx.FrameTarget(TextureFormat.Depth32Float));
        var tilesX = ((int)_ctx.Width + Visibility.TileSize - 1) / Visibility.TileSize;
        var tilesY = ((int)_ctx.Height + Visibility.TileSize - 1) / Visibility.TileSize;
        if (_depthTiles.IsValid && tilesX == _tilesX && tilesY == _tilesY) return;
        if (_depthTiles.IsValid) _ctx.Renderer.DestroyBuffer(_depthTiles);
        _tilesX = tilesX;
        _tilesY = tilesY;
        _depthTiles = _ctx.Renderer.CreateBuffer(new BufferDesc("PbrOcclusionDepthTiles", DepthTileBytes,
            BufferUsage.Storage | BufferUsage.CopySrc));
    }

    public void Setup(in FrameContext frame)
    {
        Active = _ctx.Scene.Visibility.OcclusionEnabled && _ctx.Opaque.Count > 0;
        DrawCount = Active ? _ctx.Opaque.Count : 0;
        if (!Active) return;
        for (var i = 0; i < DrawCount; i++)
        {
            var (instance, primitive, _) = _ctx.Opaque[i];
            var b = new DrawBoundsGpu { IndexCount = primitive.IndexCount, Visible = _frustum.OpaqueVisible(i) ? 1u : 0u };
            if (b.Visible != 0 && _frustum.HasReliableBounds(primitive)
                && Visibility.TryProject(primitive.LocalMin, primitive.LocalMax, instance.Model * _ctx.ViewProjection,
                    _ctx.Width, _ctx.Height, out b.Rectangle, out b.Nearest)) b.Projected = 1;
            _bounds[i] = b;
        }
        var uniforms = new OcclusionUniformsGpu
        {
            Screen = new Vector4(_ctx.Width, _ctx.Height, _tilesX, _tilesY),
            Counts = new Vector4(DrawCount, 0f, 0f, 0f),
        };
        _ctx.Renderer.UpdateBuffer<OcclusionUniformsGpu>(_uniforms, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));
        _ctx.Renderer.UpdateBuffer<DrawBoundsGpu>(_boundsBuffer, 0, Bounds);

        // A dedicated depth pass excludes cutouts and displaced shaders: treating their holes or
        // undisplaced geometry as solid would hide visible surfaces behind them.
        var depth = frame.Graph.Texture(PbrTargets.OccluderDepth);
        frame.Graph.AddRasterPass("Visibility.OccluderDepth", RenderPassEvent.AfterPrepass)
            .Depth(depth, LoadOp.Clear, clear: 1f)
            .Record(this, RecordDepth);
        var tiles = frame.Graph.ImportBuffer(_depthTiles, GraphResourceScope.GraphOnly);
        frame.Graph.AddComputePass("Visibility.ReduceDepth", RenderPassEvent.BeforeOpaque, -20)
            .BindGroup(0, "PbrOcclusionReduce", _reduceGroup,
            [
                GraphBinding.Buffer(0, _uniforms, 0, 32),
                GraphBinding.Texture(1, depth),
                GraphBinding.TrackedBuffer(2, tiles, 0, DepthTileBytes, write: true),
            ])
            .Record(this, RecordReduce);
        _graphArguments = frame.Graph.ImportBuffer(IndirectBuffer, GraphResourceScope.GraphOnly);
        frame.Graph.AddComputePass("Visibility.CullDraws", RenderPassEvent.BeforeOpaque, -10)
            .BindGroup(0, "PbrOcclusionCull", _cullGroup,
            [
                GraphBinding.Buffer(0, _uniforms, 0, 32),
                GraphBinding.Buffer(1, _boundsBuffer, 0, BoundsBytes),
                GraphBinding.TrackedBuffer(2, tiles, 0, DepthTileBytes),
                GraphBinding.TrackedBuffer(3, _graphArguments, 0, IndirectBufferBytes, write: true),
            ])
            .Record(this, RecordCull);
    }

    internal void DeclareRead(FrameGraph.PassBuilder pass)
    {
        if (Active) pass.Reads(_graphArguments);
    }

    private static void RecordDepth(OcclusionCullingFeature self, ref PassRecording pass, int _)
    {
        var ctx = self._ctx;
        ref var encoder = ref pass.Encoder;
        encoder.SetPipeline(self._depthPipeline);
        for (var i = 0; i < ctx.Opaque.Count; i++)
        {
            var primitive = ctx.Opaque[i].Primitive;
            if (!self._frustum.OpaqueVisible(i) || primitive.Skinned || primitive.Dynamic || !ctx.Materials.IsOccluder(primitive.MaterialId)) continue;
            encoder.SetBindGroup(0, ctx.DrawGroup, (uint)i * ctx.DrawStride);
            encoder.SetVertexBuffer(0, primitive.VertexBuffer, 0, primitive.VertexByteLength);
            encoder.SetIndexBuffer(primitive.IndexBuffer, IndexFormat.Uint32, 0, primitive.IndexByteLength);
            encoder.DrawIndexed(new DrawIndexedCommand(primitive.IndexCount, 1, 0, 0, 0));
        }
    }

    private static void RecordReduce(OcclusionCullingFeature self, ref PassRecording pass, int _)
    {
        pass.Encoder.SetComputePipeline(self._reducePipeline);
        pass.SetBindGroup(0);
        pass.Encoder.Dispatch(new DispatchCommand((uint)(self._tilesX * self._tilesY + 63) / 64, 1, 1));
    }

    private static void RecordCull(OcclusionCullingFeature self, ref PassRecording pass, int _)
    {
        pass.Encoder.SetComputePipeline(self._cullPipeline);
        pass.SetBindGroup(0);
        pass.Encoder.Dispatch(new DispatchCommand((uint)(self.DrawCount + 63) / 64, 1, 1));
    }

    public void Dispose()
    {
        _ctx.Renderer.DestroyPipeline(_depthPipeline);
        _ctx.Renderer.DestroyComputePipeline(_reducePipeline);
        _ctx.Renderer.DestroyComputePipeline(_cullPipeline);
        _ctx.Renderer.DestroyBuffer(_uniforms);
        _ctx.Renderer.DestroyBuffer(_boundsBuffer);
        _ctx.Renderer.DestroyBuffer(IndirectBuffer);
        _ctx.Renderer.DestroyBuffer(_depthTiles);
    }
}
