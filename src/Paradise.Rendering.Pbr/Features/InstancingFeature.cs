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
    private BufferHandle _buffer;
    private BindGroupHandle _group;
    private ShaderProgramDesc? _program;
    private bool _uploadPrepared;
    private InstanceBatch[] _opaqueBatches = [];
    private InstanceBatch[] _blendBatches = [];

    internal InstancingFeature(PbrContext ctx) => _ctx = ctx;

    public FeatureDefinition Definition => PbrFeatures.Instancing;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>Number of geometry draw calls submitted by the scene pass this frame.</summary>
    public int DrawCalls => _ctx.Frame.DrawStatistics.DrawCalls;

    /// <summary>Number of primitive instances submitted through multi-instance draw calls this frame.</summary>
    public int BatchedInstances => _ctx.Frame.DrawStatistics.BatchedInstances;

    /// <summary>Number of scene draw calls saved by instancing this frame.</summary>
    public int SavedDrawCalls => _ctx.Frame.DrawStatistics.SavedDrawCalls;

    public void Resize(uint width, uint height) { }

    public void Setup(in FrameContext frame)
    {
        _uploadPrepared = false;
        if (!_ctx.Frame.InstancingEnabled) return;
        frame.Blackboard.TryGet(VisibilityFrameData.Key, out var visibility);
        frame.Blackboard.TryGet(OcclusionFrameData.Key, out var occlusion);
        var resourcesPrepared = false;
        var opaque = PrepareBucket(_ctx.Opaque, ref _opaqueBatches, visibility,
            occlusion.Active, BlendMode.Opaque, ref resourcesPrepared);
        var blend = PrepareBucket(_ctx.Blend, ref _blendBatches, visibility,
            false, BlendMode.AlphaBlend, ref resourcesPrepared);
        _uploadPrepared = resourcesPrepared;
        frame.Blackboard.Publish(InstanceDrawPlan.Key,
            new InstanceDrawPlan(opaque, blend, resourcesPrepared ? _group : default));
    }

    private ReadOnlyMemory<InstanceBatch> PrepareBucket(List<FrameDraw> bucket, ref InstanceBatch[] batches,
        in VisibilityFrameData visibility, bool indirect, BlendMode blend, ref bool resourcesPrepared)
    {
        var hasBatches = false;
        var opaque = blend == BlendMode.Opaque;
        for (var first = 0; first < bucket.Count;)
        {
            var visible = opaque ? visibility.OpaqueVisible(first) : visibility.BlendVisible(first);
            var count = !visible || indirect ? 1 : RunLength(bucket, first, visibility, opaque);
            if (count > 1)
            {
                var primitive = bucket[first].Primitive;
                var programId = _ctx.Materials.GetProgramId(primitive.MaterialId);
                if (primitive.Skinned && programId != 0)
                    throw new InvalidOperationException(
                        $"Material program {programId} is rigid-only, but it is assigned to a skinned primitive. " +
                        "Custom material programs do not support the skinned vertex path (v1).");
                if (!resourcesPrepared)
                {
                    EnsureResources();
                    resourcesPrepared = true;
                }
                if (!hasBatches)
                {
                    if (batches.Length < bucket.Count)
                        batches = new InstanceBatch[DrawBufferCapacity.Grow(batches.Length, bucket.Count)];
                    else
                        Array.Clear(batches, 0, bucket.Count);
                    hasBatches = true;
                }
                batches[first] = new InstanceBatch(count, _ctx.Programs.GetInstancedPipeline(programId, primitive.Skinned, blend, _ctx.Materials.UsesProcedural(primitive.MaterialId), _ctx.Materials.TextureFeatures(primitive.MaterialId)));
            }
            first += count;
        }
        return hasBatches ? batches.AsMemory(0, bucket.Count) : ReadOnlyMemory<InstanceBatch>.Empty;
    }

    private int RunLength(List<FrameDraw> bucket,
        int first, in VisibilityFrameData visibility, bool opaque)
    {
        var draw = bucket[first];
        var primitive = draw.Primitive;
        if (!_ctx.Materials.SupportsInstancing(primitive.MaterialId)) return 1;
        var skinned = _ctx.Frame.IsSkinned(draw);
        var end = first + 1;
        while (end < bucket.Count)
        {
            var next = bucket[end];
            if (!(opaque ? visibility.OpaqueVisible(end) : visibility.BlendVisible(end)) || next.Primitive != primitive ||
                _ctx.Frame.IsSkinned(next) != skinned) break;
            end++;
        }
        return end - first;
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
        if (!_uploadPrepared) return;
        var data = _ctx.Frame.InstanceData(_ctx.DrawCapacity, _ctx.DrawStaging, (int)_ctx.DrawStride);
        _ctx.Renderer.UpdateBuffer<DrawUniformsGpu>(_buffer, 0, data);
    }

    public void Dispose()
    {
        if (_group.IsValid) _ctx.Renderer.DestroyBindGroup(_group);
        if (_buffer.IsValid) _ctx.Renderer.DestroyBuffer(_buffer);
        _group = default;
        _buffer = default;
        _uploadPrepared = false;
    }
}
