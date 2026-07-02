using System;
using System.Runtime.InteropServices;

namespace Paradise.BLOB;

/// <summary>
/// Blob storage in unmanaged memory (<see cref="NativeMemory.AlignedAlloc"/>): unlike
/// <see cref="ManagedBlobAssetReference"/> there is no pinned GC-heap array — no GC pressure for
/// large blobs and no pinning fragmentation. Dispose frees the allocation; the finalizer is the
/// backstop if the owner never does.
/// </summary>
public unsafe class NativeBlobAssetReference : IDisposable
{
    private void* _ptr;

    public int Length { get; }

    public ref T GetValue<T>() where T : unmanaged => ref *GetUnsafePtr<T>();

    public T* GetUnsafePtr<T>() where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_ptr is null, this);
        if (Length < sizeof(T)) throw new ArgumentException("invalid generic parameter");
        return (T*)_ptr;
    }

    public NativeBlobAssetReference(ReadOnlySpan<byte> blob, int alignment = 16)
    {
        if (blob.Length == 0) throw new ArgumentException("BLOB cannot be empty");
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentException("alignment must be a positive power of two", nameof(alignment));
        Length = blob.Length;
        _ptr = NativeMemory.AlignedAlloc((nuint)blob.Length, (nuint)alignment);
        blob.CopyTo(new Span<byte>(_ptr, blob.Length));
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~NativeBlobAssetReference() => Free();

    private void Free()
    {
        if (_ptr is null) return;
        NativeMemory.AlignedFree(_ptr);
        _ptr = null;
    }
}

/// <summary>Typed variant of <see cref="NativeBlobAssetReference"/>.</summary>
public unsafe class NativeBlobAssetReference<T> : IDisposable where T : unmanaged
{
    private void* _ptr;

    public int Length { get; }

    public ref T Value => ref *UnsafePtr;

    public T* UnsafePtr
    {
        get
        {
            ObjectDisposedException.ThrowIf(_ptr is null, this);
            return (T*)_ptr;
        }
    }

    public NativeBlobAssetReference(ReadOnlySpan<byte> blob, int alignment = 16)
    {
        if (blob.Length == 0) throw new ArgumentException("BLOB cannot be empty");
        if (blob.Length < sizeof(T)) throw new ArgumentException($"BLOB smaller than {typeof(T).Name}");
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentException("alignment must be a positive power of two", nameof(alignment));
        Length = blob.Length;
        _ptr = NativeMemory.AlignedAlloc((nuint)blob.Length, (nuint)alignment);
        blob.CopyTo(new Span<byte>(_ptr, blob.Length));
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~NativeBlobAssetReference() => Free();

    private void Free()
    {
        if (_ptr is null) return;
        NativeMemory.AlignedFree(_ptr);
        _ptr = null;
    }
}
