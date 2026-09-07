using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Forward+ light culling at <see cref="RenderPassEvent.BeforeOpaque"/>: a compute pass
/// that bins every point and spot light into the froxel grid the scene's fragment shader tests
/// before it shades a light. One thread per froxel, one bit per light.
///
/// <para>The grid is Godot's — 32×32 pixel tiles by 32 logarithmic depth slices — and the binning
/// is conservative: a froxel that claims a light it does not quite touch costs a shading add whose
/// attenuation is near zero, while one that misses a light it does touch changes pixels. Inclusion
/// wins wherever the two trade off.</para>
///
/// <para>Off, nothing bins and <see cref="SceneFeature"/> retracts the grid, so every light shades
/// every pixel — the picture is unchanged and only the cost moves.</para></summary>
public sealed class LightCullingFeature : IRenderFeature
{
    /// <summary>Mirror of <c>CullUniforms</c> in lightCull.slang.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 112)]
    private struct CullUniformsGpu
    {
        public Matrix4x4 InvProjection;
        public Vector4 Params; // x near, y far, z tile size px, w light count
        public Vector4 Screen; // x width, y height, z tilesX, w tilesY
        public Vector4 Grid;   // x zSlices, y froxel count, zw unused
    }

    private const int SliceDepthCount = ClusterBinning.ZSlices + 1;
    private const uint CullWorkgroup = 64;

    private readonly PbrContext _ctx;
    private readonly ComputePipelineHandle _pipeline;
    private readonly BindGroupLayoutDesc _group0;
    private readonly BufferHandle _uniformBuffer;
    private readonly BufferHandle _lightBuffer;
    private readonly BufferHandle _sliceDepthBuffer;
    private readonly CullLightGpu[] _lights = new CullLightGpu[FrameUniformsGpu.MaxSceneLights];
    private readonly float[] _sliceDepths = new float[SliceDepthCount];

    private BufferHandle _clusterBuffer;
    private int _clusterWords;
    private int _tilesX;
    private int _tilesY;
    private int _lightCount;
    // Defaults matter: they are what the frame uniforms carry until the first Setup extracts the
    // real pair from a projection, and a zero near would divide by zero in the slice depths.
    private float _near = 0.05f;
    private float _far = 100f;

    internal LightCullingFeature(PbrContext ctx)
    {
        _ctx = ctx;
        var program = ShaderPrograms.Load("Shaders.lightCull");
        _pipeline = ctx.Renderer.CreateComputePipeline(program);
        _group0 = ShaderPrograms.FindGroup(program, 0);
        _uniformBuffer = ctx.Renderer.CreateBuffer(new BufferDesc(
            "PbrLightCullUniforms", (ulong)Unsafe.SizeOf<CullUniformsGpu>(),
            BufferUsage.Uniform | BufferUsage.CopyDst));
        // Fixed size: the light budget is the mask width, so this array never grows.
        _lightBuffer = ctx.Renderer.CreateBuffer(new BufferDesc(
            "PbrLightCullLights", (ulong)(_lights.Length * Unsafe.SizeOf<CullLightGpu>()),
            BufferUsage.Storage | BufferUsage.CopyDst));
        _sliceDepthBuffer = ctx.Renderer.CreateBuffer(new BufferDesc(
            "PbrLightCullSliceDepths", SliceDepthCount * sizeof(float),
            BufferUsage.Storage | BufferUsage.CopyDst));
        EnsureClusterBuffer();
    }

    public FeatureDefinition Definition => PbrFeatures.LightCulling;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>Whether the masks in <see cref="ClusterBuffer"/> describe THIS frame. False while
    /// the feature is switched off, which is how <see cref="SceneFeature"/> knows to retract the
    /// grid instead of letting the shader test stale bits.</summary>
    internal bool Active { get; private set; } = true;

    /// <summary>The froxel mask buffer every lit draw reads through group 1. Always a real buffer,
    /// from construction, so the scene binds an object rather than a null even in a frame this
    /// feature never ran.</summary>
    internal BufferHandle ClusterBuffer => _clusterBuffer;

    internal ulong ClusterBufferBytes => (ulong)_clusterWords * sizeof(uint);

    internal int TilesX => _tilesX;
    internal int TilesY => _tilesY;
    internal static int ZSlices => ClusterBinning.ZSlices;

    /// <summary>The camera near and far the slices span, extracted from the frame's projection.
    /// The fragment shader slices with the same pair, so they ride the frame uniforms.</summary>
    internal float Near => _near;

    internal float Far => _far;

    public void Resize(uint width, uint height) => EnsureClusterBuffer();

    /// <summary>Switched off, the masks stop being rebuilt while every lit draw keeps reading
    /// them — a camera that then moves would shade against the froxels of whatever frame ran
    /// last, dropping lights that have since come into view. Clearing this is what makes the
    /// scene fall back to testing every light.</summary>
    public void OnEnabledChanged(bool enabled) => Active = enabled;

    private void EnsureClusterBuffer()
    {
        var tilesX = ClusterBinning.TilesFor(_ctx.Width);
        var tilesY = ClusterBinning.TilesFor(_ctx.Height);
        if (tilesX == _tilesX && tilesY == _tilesY && _clusterBuffer.IsValid) return;
        if (_clusterBuffer.IsValid) _ctx.Renderer.DestroyBuffer(_clusterBuffer);
        _tilesX = tilesX;
        _tilesY = tilesY;
        _clusterWords = tilesX * tilesY * ClusterBinning.ZSlices * ClusterBinning.MaskWordsPerFroxel;
        // CopySrc so a test can read the masks back and compare them with ClusterBinning — the
        // agreement that keeps the shader and its CPU twin one algorithm.
        _clusterBuffer = _ctx.Renderer.CreateBuffer(new BufferDesc(
            "PbrClusterMasks", (ulong)_clusterWords * sizeof(uint),
            BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc));
    }

    public void Setup(in FrameContext frame)
    {
        var scene = _ctx.Scene;
        EnsureClusterBuffer();
        ExtractDepthRange(scene.Camera.Projection);
        UploadLights(scene);

        ClusterBinning.FillSliceDepths(_near, _far, _sliceDepths);
        _ctx.Renderer.UpdateBuffer<float>(_sliceDepthBuffer, 0, _sliceDepths);

        var froxels = _tilesX * _tilesY * ClusterBinning.ZSlices;
        var uniforms = new CullUniformsGpu
        {
            InvProjection = Matrix4x4.Invert(scene.Camera.Projection, out var inverse) ? inverse : Matrix4x4.Identity,
            Params = new Vector4(_near, _far, ClusterBinning.TileSize, _lightCount),
            Screen = new Vector4(_ctx.Width, _ctx.Height, _tilesX, _tilesY),
            Grid = new Vector4(ClusterBinning.ZSlices, froxels, 0f, 0f),
        };
        _ctx.Renderer.UpdateBuffer<CullUniformsGpu>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        // GraphOnly + NeverCull, the Gi.Update shape: the consumer is the scene pass's plain
        // binding of this buffer, which carries no edge, so reachability cannot see it. Ordering
        // is by event — BeforeOpaque sorts ahead of the Opaque pass that reads the masks.
        var masks = frame.Graph.ImportBuffer(_clusterBuffer, GraphResourceScope.GraphOnly);
        frame.Graph.AddComputePass("LightCull.Bin", RenderPassEvent.BeforeOpaque)
            .BindGroup(0, "PbrLightCullGroup", _group0,
            [
                GraphBinding.Buffer(0, _uniformBuffer, 0, (ulong)Unsafe.SizeOf<CullUniformsGpu>()),
                GraphBinding.Buffer(1, _lightBuffer, 0, (ulong)(_lights.Length * Unsafe.SizeOf<CullLightGpu>())),
                GraphBinding.TrackedBuffer(2, masks, 0, ClusterBufferBytes, write: true),
                GraphBinding.Buffer(3, _sliceDepthBuffer, 0, SliceDepthCount * sizeof(float)),
            ])
            .NeverCull()
            .Record(this, Record, froxels);
    }

    /// <summary>Near and far from the row-vector perspective projection (M33 = f/(n−f),
    /// M43 = n·f/(n−f)). A degenerate extraction — an orthographic or hand-built projection —
    /// keeps the previous pair rather than producing a grid nothing can be binned into.</summary>
    private void ExtractDepthRange(in Matrix4x4 projection)
    {
        if (MathF.Abs(projection.M33) <= 1e-6f || MathF.Abs(projection.M33 + 1f) <= 1e-6f) return;
        var near = projection.M43 / projection.M33;
        var far = projection.M43 / (projection.M33 + 1f);
        if (near > 0f && far > near)
        {
            _near = near;
            _far = far;
        }
    }

    /// <summary>Packs the frame's point and spot lights into view space. Directional lights are
    /// left out rather than skipped in the shader: they reach every froxel, so binning them would
    /// only cost every thread a branch and every mask a bit that is always set.</summary>
    private void UploadLights(PbrScene scene)
    {
        var view = scene.Camera.View;
        _lightCount = 0;
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            var light = scene.Lights[i];
            if (light.Type == PbrLightType.Directional) continue;
            _lights[_lightCount++] = new CullLightGpu
            {
                ViewPositionRange = new Vector4(Vector3.Transform(light.Position, view), MathF.Max(light.Range, 0.01f)),
                Slot = (uint)i,
            };
        }
        if (_lightCount > 0) _ctx.Renderer.UpdateBuffer<CullLightGpu>(_lightBuffer, 0, _lights.AsSpan(0, _lightCount));
    }

    /// <summary>The lights this frame binned, in the layout the shader read them — what a test
    /// hands <see cref="ClusterBinning.Bin"/> to reproduce the masks.</summary>
    internal ReadOnlySpan<CullLightGpu> LightsForTest => _lights.AsSpan(0, _lightCount);

    internal ClusterGrid GridForTest =>
        ClusterGrid.For(_ctx.Scene.Camera.Projection, _ctx.Width, _ctx.Height, _near, _far);

    private static void Record(LightCullingFeature self, ref PassRecording pass, int froxels)
    {
        pass.Encoder.SetComputePipeline(self._pipeline);
        pass.SetBindGroup(0);
        pass.Encoder.Dispatch(new DispatchCommand(((uint)froxels + CullWorkgroup - 1) / CullWorkgroup, 1, 1));
    }

    public void Dispose()
    {
        var renderer = _ctx.Renderer;
        renderer.DestroyComputePipeline(_pipeline);
        renderer.DestroyBuffer(_uniformBuffer);
        renderer.DestroyBuffer(_lightBuffer);
        renderer.DestroyBuffer(_sliceDepthBuffer);
        if (_clusterBuffer.IsValid) renderer.DestroyBuffer(_clusterBuffer);
    }
}
