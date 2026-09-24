using System;

namespace Paradise.BLOB;

public unsafe struct BlobArray<T> where T : unmanaged
{
    internal int Offset;
    private int _length;

    public int Length { get => _length; internal set => _length = value; }

    public ref T this[int index]
    {
        get
        {
            if (index < 0 || index >= Length) throw new ArgumentOutOfRangeException($"index({index}) out of range[0-{Length})");
            return ref *(UnsafePtr + index);
        }
    }

    public T* UnsafePtr
    {
        get
        {
            fixed (void* thisPtr = &Offset)
            {
                return (T*)((byte*) thisPtr + Offset);
            }
        }
    }

    public T[] ToArray() => ToSpan().ToArray();

    public Span<T> ToSpan()
    {
        return new Span<T>(UnsafePtr, Length);
    }
}
