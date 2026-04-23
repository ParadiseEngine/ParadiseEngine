namespace Paradise.Rendering;

/// <summary>Non-indexed draw call parameters.</summary>
public readonly struct DrawCommand
{
    public readonly uint VertexCount;
    public readonly uint InstanceCount;
    public readonly uint FirstVertex;
    public readonly uint FirstInstance;

    public DrawCommand(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
    {
        VertexCount = vertexCount;
        InstanceCount = instanceCount;
        FirstVertex = firstVertex;
        FirstInstance = firstInstance;
    }
}

/// <summary>Indexed draw call parameters.</summary>
public readonly struct DrawIndexedCommand
{
    public readonly uint IndexCount;
    public readonly uint InstanceCount;
    public readonly uint FirstIndex;
    public readonly int BaseVertex;
    public readonly uint FirstInstance;

    public DrawIndexedCommand(uint indexCount, uint instanceCount, uint firstIndex, int baseVertex, uint firstInstance)
    {
        IndexCount = indexCount;
        InstanceCount = instanceCount;
        FirstIndex = firstIndex;
        BaseVertex = baseVertex;
        FirstInstance = firstInstance;
    }
}

/// <summary>Compute dispatch parameters.</summary>
public readonly struct DispatchCommand
{
    public readonly uint WorkgroupCountX;
    public readonly uint WorkgroupCountY;
    public readonly uint WorkgroupCountZ;

    public DispatchCommand(uint workgroupCountX, uint workgroupCountY, uint workgroupCountZ)
    {
        WorkgroupCountX = workgroupCountX;
        WorkgroupCountY = workgroupCountY;
        WorkgroupCountZ = workgroupCountZ;
    }
}
