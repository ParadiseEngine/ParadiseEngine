using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr;

// Depth passes do not bind materials, but buffer ranges and the effective vertex path still
// have to agree. In particular, a rigid and a skinned stream never share a batch.
internal readonly record struct DepthGeometry(
    BufferHandle VertexBuffer, BufferHandle IndexBuffer, uint IndexCount,
    ulong VertexByteLength, ulong IndexByteLength, bool SkinnedStream, bool Skinned)
{
    public static DepthGeometry From(PbrPrimitive primitive, bool skinned) => new(
        primitive.VertexBuffer, primitive.IndexBuffer, primitive.IndexCount,
        primitive.VertexByteLength, primitive.IndexByteLength, primitive.Skinned, skinned);
}

internal struct DepthBatch
{
    public DepthGeometry Geometry;
    public int First;
    public int Last;
    public int Count;
}

internal sealed class DepthBatching
{
    private readonly Dictionary<DepthGeometry, int> _groups = [];
    private int[] _next = [];
    public List<DepthBatch> Batches { get; } = [];

    public void Clear(int capacity)
    {
        Batches.Clear();
        _groups.Clear();
        if (_next.Length < capacity) _next = new int[DrawBufferCapacity.Grow(_next.Length, capacity)];
    }

    public void Add(int drawIndex, DepthGeometry geometry, bool reorder)
    {
        _next[drawIndex] = -1;
        var batchIndex = Batches.Count - 1;
        if (reorder ? _groups.TryGetValue(geometry, out batchIndex)
            : batchIndex >= 0 && Batches[batchIndex].Geometry == geometry)
        {
            ref var batch = ref CollectionsMarshal.AsSpan(Batches)[batchIndex];
            _next[batch.Last] = drawIndex;
            batch.Last = drawIndex;
            batch.Count++;
            return;
        }
        if (reorder) _groups.Add(geometry, Batches.Count);
        Batches.Add(new DepthBatch { Geometry = geometry, First = drawIndex, Last = drawIndex, Count = 1 });
    }

    public int Next(int drawIndex) => _next[drawIndex];
}

internal sealed class DepthInstanceBuffer<T>(IRenderer renderer, string name) : IDisposable where T : unmanaged
{
    private BufferHandle _buffer;
    public BindGroupHandle Group { get; private set; }
    public T[] Staging { get; private set; } = [];
    public int Count { get; set; }

    public void EnsureCapacity(int required, ShaderProgramDesc program)
    {
        if (required <= Staging.Length) return;
        var capacity = DrawBufferCapacity.Grow(Staging.Length, required);
        var bytes = (ulong)capacity * (ulong)Unsafe.SizeOf<T>();
        var buffer = renderer.CreateBuffer(new BufferDesc(name, bytes, BufferUsage.Storage | BufferUsage.CopyDst));
        BindGroupHandle group;
        try
        {
            group = renderer.CreateBindGroup(new BindGroupDesc(name, ShaderPrograms.FindGroup(program, 0),
                new[] { BindGroupEntryDesc.ForBuffer(0, buffer, 0, bytes) }));
        }
        catch
        {
            renderer.DestroyBuffer(buffer);
            throw;
        }
        if (Group.IsValid) renderer.DestroyBindGroup(Group);
        if (_buffer.IsValid) renderer.DestroyBuffer(_buffer);
        Group = group;
        _buffer = buffer;
        Staging = new T[capacity];
    }

    public void Upload()
    {
        if (Count > 0) renderer.UpdateBuffer<T>(_buffer, 0, Staging.AsSpan(0, Count));
    }

    public void Dispose()
    {
        if (Group.IsValid) renderer.DestroyBindGroup(Group);
        if (_buffer.IsValid) renderer.DestroyBuffer(_buffer);
    }
}
