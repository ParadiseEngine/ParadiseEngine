using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>Investigation-only identity of one emitted scene draw, including its instanced batch.</summary>
public readonly record struct PbrDrawProbe(
    int Ordinal, int MaterialId, int ProgramId, BufferHandle VertexBuffer, BufferHandle IndexBuffer,
    uint IndexCount, int InstanceCount, int FirstSlot, bool Skinned, bool Transparent, Vector3 Position);
