using System;
using System.IO;

namespace Paradise.BLOB;

public class BlobMemoryStream : IBlobStream, IDisposable
{
    private readonly MemoryStream _stream;

    public BlobMemoryStream() : this (1024 * 4) {}
    public BlobMemoryStream(int capacity) => _stream = new MemoryStream(capacity);

    public int Alignment { get; set; } = 4;
    
    public int PatchPosition { get; set; }

    public int Position
    {
        get => (int)_stream.Position;
        set => _stream.Position = value;
    }

    public int Length
    {
        get => (int)_stream.Length;
        set => _stream.SetLength(value);
    }

    public byte[] ToArray() => _stream.ToArray();

    public byte[] Buffer => _stream.GetBuffer();

    public unsafe void Write(byte* valuePtr, int size, int alignment)
    {
        PatchPosition = Math.Max(PatchPosition, (int)Utilities.Align(Position + size, this.GetAlignment(alignment)));
        _stream.Write(new System.ReadOnlySpan<byte>(valuePtr, size));
    }

    public void Dispose()
    {
        _stream?.Dispose();
    }
}
