using Paradise.ECS;

namespace Paradise.Rendering.ECS.Systems;

/// <summary>
/// ECS chunk system that reads <see cref="LocalToWorld"/>, <see cref="MeshRef"/>, and
/// <see cref="MaterialRef"/> from every matching chunk and appends one
/// <see cref="ExtractedRenderable"/> per entity to the current frame's
/// <see cref="ExtractionContext.Packets"/>.
/// <para>
/// Wire this into the extraction <see cref="SystemSchedule{TMask,TConfig}"/> and set
/// <see cref="ExtractionContext.Packets"/> before calling <c>Run()</c>:
/// <code>
/// ExtractionContext.Packets = packets;
/// packets.Reset();
/// extractSchedule.Run();
/// </code>
/// </para>
/// </summary>
public ref partial struct ExtractRenderablesSystem : IChunkSystem
{
    public ReadOnlySpan<LocalToWorld> LocalToWorlds;
    public ReadOnlySpan<MeshRef> MeshRefs;
    public ReadOnlySpan<MaterialRef> MaterialRefs;

    public void ExecuteChunk()
    {
        var packets = ExtractionContext.Packets!;
        for (int i = 0; i < LocalToWorlds.Length; i++)
        {
            packets.AppendRenderable(new ExtractedRenderable
            {
                LocalToWorld = LocalToWorlds[i].Value,
                MeshId = MeshRefs[i].MeshId,
                MaterialId = MaterialRefs[i].MaterialId,
            });
        }
    }
}
