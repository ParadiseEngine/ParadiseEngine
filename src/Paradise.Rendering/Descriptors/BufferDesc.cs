namespace Paradise.Rendering;

/// <summary>Creation parameters for a GPU buffer.</summary>
public readonly struct BufferDesc
{
    public readonly string? Name;
    public readonly ulong Size;
    public readonly BufferUsage Usage;
    public readonly bool MappedAtCreation;

    public BufferDesc(string? name, ulong size, BufferUsage usage, bool mappedAtCreation = false)
    {
        Name = name;
        Size = size;
        Usage = usage;
        MappedAtCreation = mappedAtCreation;
    }
}
