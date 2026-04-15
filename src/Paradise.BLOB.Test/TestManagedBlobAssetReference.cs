using System;
using System.Runtime.InteropServices;

namespace Paradise.BLOB.Test;

public class TestManagedBlobAssetReference
{
    struct SimpleData
    {
        public int X;
        public float Y;
    }

    private static byte[] CreateBlob(int x, float y)
    {
        var builder = new ValueBuilder<SimpleData>();
        builder.Value.X = x;
        builder.Value.Y = y;
        return builder.CreateBlob();
    }

    [Test]
    public unsafe void should_construct_and_access_value()
    {
        var blob = CreateBlob(42, 3.14f);
        using var reference = new ManagedBlobAssetReference<SimpleData>(blob);

        Assert.AreEqual(42, reference.Value.X);
        Assert.AreEqual(3.14f, reference.Value.Y);
    }

    [Test]
    public unsafe void should_access_via_unsafe_ptr()
    {
        var blob = CreateBlob(99, 1.5f);
        using var reference = new ManagedBlobAssetReference<SimpleData>(blob);

        SimpleData* ptr = reference.UnsafePtr;
        Assert.AreEqual(99, ptr->X);
        Assert.AreEqual(1.5f, ptr->Y);
    }

    [Test]
    public void should_not_throw_on_double_dispose()
    {
        var blob = CreateBlob(1, 2.0f);
        var reference = new ManagedBlobAssetReference<SimpleData>(blob);

        reference.Dispose();
        reference.Dispose();
    }

    [Test]
    public unsafe void should_return_valid_pointer_from_GetUnsafePtr()
    {
        var data = new SimpleData { X = 42, Y = 3.14f };
        var bytes = new byte[sizeof(SimpleData)];
        fixed (byte* dst = bytes)
        {
            *(SimpleData*)dst = data;
        }

        using var blob = new ManagedBlobAssetReference(bytes);
        var ptr = blob.GetUnsafePtr<SimpleData>();

        Assert.AreEqual(42, ptr->X);
        Assert.AreEqual(3.14f, ptr->Y);
    }

    [Test]
    public unsafe void should_return_stable_pointer_across_calls()
    {
        var bytes = new byte[sizeof(int)];
        fixed (byte* dst = bytes)
        {
            *(int*)dst = 123;
        }

        using var blob = new ManagedBlobAssetReference(bytes);
        var ptr1 = blob.GetUnsafePtr<int>();
        var ptr2 = blob.GetUnsafePtr<int>();

        Assert.AreEqual((long)ptr1, (long)ptr2, "Pointer should be stable across calls");
        Assert.AreEqual(123, *ptr1);
    }

    [Test]
    public unsafe void should_return_correct_value_from_GetValue()
    {
        var data = new SimpleData { X = 99, Y = 2.71f };
        var bytes = new byte[sizeof(SimpleData)];
        fixed (byte* dst = bytes)
        {
            *(SimpleData*)dst = data;
        }

        using var blob = new ManagedBlobAssetReference(bytes);
        ref var value = ref blob.GetValue<SimpleData>();

        Assert.AreEqual(99, value.X);
        Assert.AreEqual(2.71f, value.Y);
    }

    [Test]
    public void should_allow_double_dispose_without_throwing()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var blob = new ManagedBlobAssetReference(bytes);
        blob.Dispose();
        blob.Dispose();
    }

    [Test]
    public void should_reject_empty_blob_array()
    {
        Assert.Catch<ArgumentException>(() => new ManagedBlobAssetReference(Array.Empty<byte>()));
    }

    [Test]
    public unsafe void should_throw_when_blob_too_small_for_type()
    {
        var bytes = new byte[] { 1, 2 };
        using var blob = new ManagedBlobAssetReference(bytes);
        Assert.Catch<ArgumentException>(() => blob.GetUnsafePtr<int>());
    }
}
