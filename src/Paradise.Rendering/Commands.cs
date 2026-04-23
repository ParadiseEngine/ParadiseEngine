namespace Paradise.Rendering;

/// <summary>Non-indexed draw call parameters.</summary>
public readonly record struct DrawCommand(
    uint VertexCount,
    uint InstanceCount,
    uint FirstVertex,
    uint FirstInstance);

/// <summary>Indexed draw call parameters.</summary>
public readonly record struct DrawIndexedCommand(
    uint IndexCount,
    uint InstanceCount,
    uint FirstIndex,
    int BaseVertex,
    uint FirstInstance);

/// <summary>Compute dispatch parameters.</summary>
public readonly record struct DispatchCommand(
    uint WorkgroupCountX,
    uint WorkgroupCountY,
    uint WorkgroupCountZ);
