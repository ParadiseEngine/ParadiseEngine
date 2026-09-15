using System.Runtime.CompilerServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Batches consecutive compatible draws using a storage buffer of per-instance transforms.</summary>
/// <remarks>Opaque regrouping is opt-in; sorted transparency and custom material vertex paths
/// stay intact. All camera passes use the packed frame's draw slots.</remarks>
public sealed class InstancingFeature : IRenderFeature
{
    private readonly PbrContext _ctx;
    private int _capacity;
    private readonly Dictionary<(bool Skinned, BlendMode Blend), PipelineHandle> _pipelines = [];
    private BufferHandle _buffer;
    private BindGroupHandle _group;
    private ShaderProgramDesc? _program;
    private bool _active;

    internal InstancingFeature(PbrContext ctx) => _ctx = ctx;

    public FeatureDefinition Definition => PbrFeatures.Instancing;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>Number of geometry draw calls submitted by the scene pass this frame.</summary>
    public int DrawCalls { get; private set; }

    /// <summary>Number of primitive instances submitted through multi-instance draw calls this frame.</summary>
    public int BatchedInstances { get; private set; }

    /// <summary>Number of scene draw calls saved by instancing this frame.</summary>
    public int SavedDrawCalls { get; private set; }

    public void Resize(uint width, uint height) { }

    public void Setup(in FrameContext frame)
    {
        _active = _ctx.Scene.Instancing.Enabled;
        ResetStatistics();
    }

    public void OnEnabledChanged(bool enabled)
    {
        _active = false;
        ResetStatistics();
    }

    internal void ResetStatistics()
    {
        DrawCalls = 0;
        BatchedInstances = 0;
        SavedDrawCalls = 0;
    }

    internal int RunLength(List<FrameDraw> bucket,
        int first, FrustumCullingFeature frustum, bool opaque)
    {
        var draw = bucket[first];
        var primitive = draw.Primitive;
        if (!_active || _ctx.Materials.GetProgramId(primitive.MaterialId) != 0) return 1;
        var skinned = _ctx.Frame.IsSkinned(draw);
        var end = first + 1;
        while (end < bucket.Count)
        {
            var next = bucket[end];
            if (!(opaque ? frustum.OpaqueVisible(end) : frustum.BlendVisible(end)) || next.Primitive != primitive ||
                _ctx.Frame.IsSkinned(next) != skinned) break;
            end++;
        }
        return end - first;
    }

    internal void CountDraw(int instances)
    {
        DrawCalls++;
        if (instances < 2) return;
        BatchedInstances += instances;
        SavedDrawCalls += instances - 1;
    }

    internal BindGroupHandle Group
    {
        get
        {
            EnsureResources();
            return _group;
        }
    }

    internal PipelineHandle Pipeline(bool skinned, BlendMode blend)
    {
        if (_pipelines.TryGetValue((skinned, blend), out var pipeline)) return pipeline;
        EnsureResources();
        pipeline = _ctx.Renderer.CreatePipeline(_program!, PbrTargets.HdrFormat,
            depthStencilFormat: TextureFormat.Depth32Float, blend: blend,
            depthWriteEnabled: blend == BlendMode.Opaque,
            vertexEntryPoint: skinned ? "vertexMainSkinned" : "vertexMain",
            fragmentEntryPoint: "fragmentMain");
        _pipelines.Add((skinned, blend), pipeline);
        return pipeline;
    }

    private void EnsureResources()
    {
        if (_buffer.IsValid && _capacity >= _ctx.DrawCapacity) return;
        if (_program is null)
        {
            _program = ShaderPrograms.Load("Shaders.pbrInstanced");
            UniformLayoutValidator.Validate(_program);
        }
        var capacity = _ctx.DrawCapacity;
        var bytes = (ulong)capacity * (ulong)Unsafe.SizeOf<DrawUniformsGpu>();
        var buffer = _ctx.Renderer.CreateBuffer(new BufferDesc("PbrInstances", bytes,
            BufferUsage.Storage | BufferUsage.CopyDst));
        BindGroupHandle group;
        try
        {
            group = _ctx.Renderer.CreateBindGroup(new BindGroupDesc("PbrInstances", ShaderPrograms.FindGroup(_program, 0),
            new[]
            {
                BindGroupEntryDesc.ForBuffer(0, _ctx.DrawUniformRing, 0, (ulong)Unsafe.SizeOf<DrawUniformsGpu>()),
                BindGroupEntryDesc.ForBuffer(1, buffer, 0, bytes),
            }));
        }
        catch
        {
            _ctx.Renderer.DestroyBuffer(buffer);
            throw;
        }
        if (_group.IsValid) _ctx.Renderer.DestroyBindGroup(_group);
        if (_buffer.IsValid) _ctx.Renderer.DestroyBuffer(_buffer);
        _capacity = capacity;
        _buffer = buffer;
        _group = group;
    }

    public void BeforeSubmit()
    {
        if (BatchedInstances == 0) return;
        _ctx.Renderer.UpdateBuffer<DrawUniformsGpu>(_buffer, 0, _ctx.Frame.Draws.AsSpan(0, _ctx.Frame.DrawCount));
    }

    public void Dispose()
    {
        foreach (var pipeline in _pipelines.Values) _ctx.Renderer.DestroyPipeline(pipeline);
        if (_group.IsValid) _ctx.Renderer.DestroyBindGroup(_group);
        if (_buffer.IsValid) _ctx.Renderer.DestroyBuffer(_buffer);
    }
}
