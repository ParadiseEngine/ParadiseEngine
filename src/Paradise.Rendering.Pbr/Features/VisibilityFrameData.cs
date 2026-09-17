using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Camera visibility indexed by the final opaque and blended draw slots.</summary>
public readonly record struct VisibilityFrameData(ReadOnlyMemory<bool> Opaque, ReadOnlyMemory<bool> Blend)
{
    public static FrameDataKey<VisibilityFrameData> Key { get; } = new("Pbr.Visibility");
    public bool OpaqueVisible(int index) => Opaque.IsEmpty || Opaque.Span[index];
    public bool BlendVisible(int index) => Blend.IsEmpty || Blend.Span[index];
}

/// <summary>GPU-written indirect arguments for the frame's opaque draw slots.</summary>
public readonly record struct OcclusionFrameData(GraphBuffer Arguments, BufferHandle IndirectBuffer, uint Stride)
{
    public static FrameDataKey<OcclusionFrameData> Key { get; } = new("Pbr.Occlusion");
    public bool Active => IndirectBuffer.IsValid;
}

/// <summary>Lighting parameters uploaded after the depth pass and dependent effects are recorded.</summary>
public readonly record struct PrepassFrameData(BufferHandle SsaoUniformBuffer)
{
    public static FrameDataKey<PrepassFrameData> Key { get; } = new("Pbr.Prepass");
}

internal static class DrawVisibility
{
    public static bool HasReliableBounds(in PbrPrimitive primitive, MaterialResourceCache materials) =>
        !primitive.Skinned && !primitive.Dynamic
        && (primitive.LocalMin != default || primitive.LocalMax != default)
        && materials.PreservesMeshBounds(primitive.MaterialId);
}

internal sealed class DrawStatistics
{
    public int DrawCalls { get; private set; }
    public int BatchedInstances { get; private set; }
    public int SavedDrawCalls { get; private set; }

    public void Reset() => DrawCalls = BatchedInstances = SavedDrawCalls = 0;

    public void CountDraw(int instances)
    {
        DrawCalls++;
        if (instances < 2) return;
        BatchedInstances += instances;
        SavedDrawCalls += instances - 1;
    }
}
