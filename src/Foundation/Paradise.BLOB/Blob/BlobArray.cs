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

    /// <summary>Whether every element lies inside the <paramref name="length"/> bytes at <paramref name="start"/>.</summary>
    /// <remarks>Computes addresses and reads nothing past this header, so a reader of untrusted
    /// bytes can check an array before following its relative offset. The header itself must
    /// already be known to lie inside the range. An empty array is always within.</remarks>
    public bool IsWithin(void* start, long length)
    {
        if (_length < 0) return false;
        if (_length == 0) return true;
        var begin = (byte*)UnsafePtr - (byte*)start;
        return begin >= 0 && begin <= length && (long)_length * sizeof(T) <= length - begin;
    }

    public T[] ToArray() => ToSpan().ToArray();

    public Span<T> ToSpan()
    {
        return new Span<T>(UnsafePtr, Length);
    }
}
