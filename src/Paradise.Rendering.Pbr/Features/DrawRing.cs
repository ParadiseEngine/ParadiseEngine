namespace Paradise.Rendering.Pbr;

/// <summary>Retains a geometrically growing uniform ring between frames.</summary>
internal sealed class DrawRing(IRenderer renderer, string name, BindGroupLayoutDesc layout, uint uniformBytes) : IDisposable
{
    public int Capacity { get; private set; }
    public BufferHandle Buffer { get; private set; }
    public BindGroupHandle Group { get; private set; }
    public byte[] Staging { get; private set; } = [];

    // Called before graph recording; previous frame data does not need copying.
    public void EnsureCapacity(int required)
    {
        if (required <= Capacity) return;
        var capacity = DrawBufferCapacity.Grow(Capacity, required);
        var staging = new byte[checked(capacity * (int)renderer.UniformBufferOffsetAlignment)];
        var buffer = renderer.CreateBuffer(new BufferDesc(name, (ulong)staging.Length,
            BufferUsage.Uniform | BufferUsage.CopyDst));
        BindGroupHandle group;
        try
        {
            group = renderer.CreateBindGroup(new BindGroupDesc(name + "Group", layout,
                new[] { BindGroupEntryDesc.ForBuffer(0, buffer, 0, uniformBytes) }));
        }
        catch
        {
            renderer.DestroyBuffer(buffer);
            throw;
        }

        // The backend defers native destruction until in-flight submissions are safe.
        if (Group.IsValid) renderer.DestroyBindGroup(Group);
        if (Buffer.IsValid) renderer.DestroyBuffer(Buffer);
        Buffer = buffer;
        Group = group;
        Staging = staging;
        Capacity = capacity;
    }

    public void Dispose()
    {
        if (Group.IsValid) renderer.DestroyBindGroup(Group);
        if (Buffer.IsValid) renderer.DestroyBuffer(Buffer);
    }
}

internal static class DrawBufferCapacity
{
    public const int Initial = 4096;

    public static int Grow(int current, int required)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(required);
        var capacity = Math.Max(current, Initial);
        while (capacity < required) capacity = checked(capacity * 2);
        return capacity;
    }
}
