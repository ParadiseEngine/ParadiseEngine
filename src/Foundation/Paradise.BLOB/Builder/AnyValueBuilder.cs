using System;

namespace Paradise.BLOB;

public class AnyValueBuilder : IBuilder
{
    private byte[]? _data;

    public int Alignment { get; set; } = 0;

    public int DataPosition { get; private set; }
    public int DataSize { get; private set; }
    public int PatchPosition { get; private set; }
    public int PatchSize { get; private set; }

    public void SetValue<T>(T value) where T : unmanaged
    {
        SetValue(value, Utilities.AlignOf<T>());
    }

    public unsafe void SetValue<T>(T value, int alignment) where T : unmanaged
    {
        SetBytes(new ReadOnlySpan<byte>(&value, sizeof(T)), alignment);
    }

    public void SetBytes(ReadOnlySpan<byte> data, int alignment)
    {
        if (!Utilities.IsPowerOfTwo(alignment)) throw new ArgumentException($"{nameof(alignment)} must be a power of two number");
        Alignment = alignment;
        _data = data.ToArray();
    }

    public void Build(IBlobStream stream)
    {
        byte[] data = _data ?? throw new InvalidOperationException("Value must be set before building.");
        DataPosition = stream.Position;
        DataSize = data.Length;
        PatchPosition = stream.PatchPosition;
        stream.WriteArrayData(data, stream.GetAlignment(Alignment));
        PatchSize = stream.PatchPosition - PatchPosition;
    }

}
