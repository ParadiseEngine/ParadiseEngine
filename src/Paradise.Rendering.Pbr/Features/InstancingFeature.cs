using System.Runtime.CompilerServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Batches consecutive compatible draws using a storage buffer of per-instance transforms.</summary>
/// <remarks>Opaque regrouping and custom shader instancing require explicit opt-in.
/// All camera passes use the frame's draw slots.</remarks>
public sealed class InstancingFeature : IRenderFeature
{
    private readonly PbrContext _ctx;
    private int _capacity;
    private readonly Dictionary<(int ProgramId, bool Skinned, BlendMode Blend), PipelineHandle> _pipelines = [];
    private BufferHandle _buffer;
    private BindGroupHandle _group;
    private ShaderProgramDesc? _program;
    private bool _active;
    private bool _uploadRequested;

    internal InstancingFeature(PbrContext ctx) => _ctx = ctx;

    public FeatureDefinition Definition => PbrFeatures.Instancing;
    public FrameRequirements Requires => FrameRequirements.None;
    internal bool Active => _active;

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
        _uploadRequested = false;
        ResetStatistics();
    }

    public void OnEnabledChanged(bool enabled)
    {
        _active = false;
        _uploadRequested = false;
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
        if (!_active || !_ctx.Materials.SupportsInstancing(primitive.MaterialId)) return 1;
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

    internal BindGroupHandle Group => RequestGroup();

    internal BindGroupHandle RequestGroup()
    {
        EnsureResources();
        _uploadRequested = true;
        return _group;
    }

    internal PipelineHandle Pipeline(int programId, bool skinned, BlendMode blend)
    {
        if (_pipelines.TryGetValue((programId, skinned, blend), out var pipeline)) return pipeline;
        EnsureResources();
        var (program, vertexEntry, fragmentEntry) = _ctx.Programs.GetInstanced(programId, skinned);
        pipeline = _ctx.Renderer.CreatePipeline(program, PbrTargets.HdrFormat,
            depthStencilFormat: TextureFormat.Depth32Float, blend: blend,
            depthWriteEnabled: blend == BlendMode.Opaque,
            vertexEntryPoint: vertexEntry,
            fragmentEntryPoint: fragmentEntry);
        _pipelines.Add((programId, skinned, blend), pipeline);
        return pipeline;
    }

    private void EnsureResources()
    {
        if (_buffer.IsValid && _capacity >= _ctx.DrawCapacity) return;
        if (_program is null)
        {
            _program = _ctx.Programs.Instanced;
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
        if (!_uploadRequested) return;
        var data = _ctx.Frame.InstanceData(_ctx.DrawCapacity, _ctx.DrawStaging, (int)_ctx.DrawStride);
        _ctx.Renderer.UpdateBuffer<DrawUniformsGpu>(_buffer, 0, data);
    }

    public void Dispose()
    {
        foreach (var pipeline in _pipelines.Values) _ctx.Renderer.DestroyPipeline(pipeline);
        if (_group.IsValid) _ctx.Renderer.DestroyBindGroup(_group);
        if (_buffer.IsValid) _ctx.Renderer.DestroyBuffer(_buffer);
    }
}
