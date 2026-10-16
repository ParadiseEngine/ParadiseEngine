using System;

namespace Paradise.BLOB;

public unsafe class PtrBuilderWithRefBuilder<TValue, TPtr> : Builder<TPtr>
    where TValue : unmanaged
    where TPtr : unmanaged
{
    private readonly IBuilder<TValue> _refBuilder;
    public IBuilder<TValue> ValueBuilder => _refBuilder;

    static PtrBuilderWithRefBuilder()
    {
        // TPtr must store a single relative offset, matching BlobPtr's layout.
        if (sizeof(TPtr) != sizeof(int))
            throw new ArgumentException($"{nameof(TPtr)} must has and only has an int `Offset` field");
    }

    public PtrBuilderWithRefBuilder(IBuilder<TValue> refBuilder)
    {
        _refBuilder = refBuilder;
    }

    protected override void BuildImpl(IBlobStream stream, ref TPtr data)
    {
        stream.WriteOffset(_refBuilder.DataPosition);
    }
}

public class PtrBuilderWithRefBuilder<T> : PtrBuilderWithRefBuilder<T, BlobPtr<T>> where T : unmanaged
{
    public PtrBuilderWithRefBuilder(IBuilder<T> builder) : base(builder) {}
}
