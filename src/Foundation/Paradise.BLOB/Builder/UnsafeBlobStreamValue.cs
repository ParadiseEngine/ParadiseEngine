using System;

namespace Paradise.BLOB;

public unsafe ref struct UnsafeBlobStreamValue<T> where T : unmanaged
{
    private readonly IBlobStream _stream;
    private readonly int _position;

    public UnsafeBlobStreamValue(IBlobStream stream) : this(stream, stream.Position) {}
    public UnsafeBlobStreamValue(IBlobStream stream, int position)
    {
        _stream = stream;
        _position = position;
        if (stream.Length < position + sizeof(T)) throw new ArgumentException("invalid position");
    }

    public ref T Value
    {
        get
        {
            fixed (void* ptr = &_stream.Buffer[_position])
            {
                return ref *(T*)ptr;
            }
        }
    }
}
