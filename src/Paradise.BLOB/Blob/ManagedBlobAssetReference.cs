using System;
using System.Runtime.InteropServices;

namespace Paradise.BLOB;

public unsafe class ManagedBlobAssetReference : IDisposable
{
    private readonly byte[] _blob;
    private GCHandle _handle;
    private bool _disposed;

    public ref T GetValue<T>() where T : unmanaged => ref *GetUnsafePtr<T>();
    public T* GetUnsafePtr<T>() where T : unmanaged
    {
        if (_blob.Length < sizeof(T)) throw new ArgumentException("invalid generic parameter");
        return (T*)_handle.AddrOfPinnedObject().ToPointer();
    }

    public int Length => _blob.Length;
    public byte[] Blob => _blob;

    public ManagedBlobAssetReference(byte[] blob)
    {
        if (blob.Length == 0) throw new ArgumentException("BLOB cannot be empty");
        _blob = blob;
        _handle = GCHandle.Alloc(_blob, GCHandleType.Pinned);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Free();
        GC.SuppressFinalize(this);
    }

    ~ManagedBlobAssetReference()
    {
        if (_disposed || !_handle.IsAllocated) return;
        _disposed = true;
        _handle.Free();
    }
}

public unsafe class ManagedBlobAssetReference<T> : IDisposable where T : unmanaged
{
    private readonly byte[] _blob;
    private GCHandle _handle;
    private bool _disposed;

    public ref T Value => ref *UnsafePtr;
    public T* UnsafePtr => (T*)_handle.AddrOfPinnedObject().ToPointer();

    public int Length => _blob.Length;
    public byte[] Blob => _blob;

    public ManagedBlobAssetReference(byte[] blob)
    {
        if (blob.Length == 0) throw new ArgumentException("BLOB cannot be empty");
        _blob = blob;
        _handle = GCHandle.Alloc(_blob, GCHandleType.Pinned);
    }

    ~ManagedBlobAssetReference()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle.IsAllocated) _handle.Free();
        GC.SuppressFinalize(this);
    }
}
