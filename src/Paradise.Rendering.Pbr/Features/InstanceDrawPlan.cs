using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>A consecutive main-pass instance run and its prepared pipeline.</summary>
public readonly record struct InstanceBatch(int Count, PipelineHandle Pipeline);

/// <summary>Prepared main-pass instance batches in the current frame's draw order.</summary>
/// <remarks>Only multi-instance run starts contain entries; other draws use the ordinary draw
/// path. The backing storage belongs to this frame and must not be retained across renders.</remarks>
public readonly record struct InstanceDrawPlan(
    ReadOnlyMemory<InstanceBatch> Opaque,
    ReadOnlyMemory<InstanceBatch> Blend,
    BindGroupHandle Group)
{
    public static FrameDataKey<InstanceDrawPlan> Key { get; } = new("Pbr.InstanceDrawPlan");

    public InstanceBatch Batch(bool opaque, int index)
    {
        var batches = opaque ? Opaque.Span : Blend.Span;
        var batch = batches.IsEmpty ? default : batches[index];
        return batch.Count > 1 ? batch : new InstanceBatch(1, default);
    }
}
