using System;

namespace Paradise.BLOB.Test;

public class TestNativeBlobAssetReference
{
    struct SimpleData
    {
        public int X;
        public float Y;
    }

    struct ArrayData
    {
        public int Header;
        public BlobArray<float> Values;
    }

    private static byte[] CreateBlob(int x, float y)
    {
        var builder = new ValueBuilder<SimpleData>();
        builder.Value.X = x;
        builder.Value.Y = y;
        return builder.CreateBlob();
    }

    [Test]
    public void should_construct_and_access_value()
    {
        using var reference = new NativeBlobAssetReference<SimpleData>(CreateBlob(42, 3.14f));

        Assert.AreEqual(42, reference.Value.X);
        Assert.AreEqual(3.14f, reference.Value.Y);
    }

    [Test]
    public void should_round_trip_struct_with_array_through_native_memory()
    {
        float[] values = { 1.5f, -2.25f, 3.125f, 0f, 99f };
        var builder = new StructBuilder<ArrayData>();
        builder.Value.Header = 7;
        builder.SetArray(ref builder.Value.Values, values);
        using var reference = builder.CreateNativeBlobAssetReference();

        Assert.AreEqual(7, reference.Value.Header);
        Assert.AreEqual(values.Length, reference.Value.Values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.AreEqual(values[i], reference.Value.Values[i]);
        }
    }

    [Test]
    public unsafe void should_allocate_with_requested_alignment_and_stable_pointer()
    {
        using var reference = new NativeBlobAssetReference<SimpleData>(CreateBlob(1, 2f), alignment: 64);

        Assert.AreEqual((nuint)0, (nuint)reference.UnsafePtr % 64);
        Assert.AreEqual((long)reference.UnsafePtr, (long)reference.UnsafePtr, "Pointer should be stable across calls");
    }

    [Test]
    public unsafe void should_access_via_untyped_reference()
    {
        using var reference = new NativeBlobAssetReference(CreateBlob(99, 2.71f));

        ref var value = ref reference.GetValue<SimpleData>();
        Assert.AreEqual(99, value.X);
        Assert.AreEqual(2.71f, value.Y);
    }

    [Test]
    public void should_throw_after_dispose()
    {
        var reference = new NativeBlobAssetReference<SimpleData>(CreateBlob(1, 2f));
        reference.Dispose();
        Assert.Catch<ObjectDisposedException>(() => _ = reference.Value.X);
    }

    [Test]
    public void should_not_throw_on_double_dispose()
    {
        var reference = new NativeBlobAssetReference<SimpleData>(CreateBlob(1, 2f));
        reference.Dispose();
        reference.Dispose();
    }

    [Test]
    public void should_reject_empty_blob_and_bad_alignment()
    {
        Assert.Catch<ArgumentException>(() => new NativeBlobAssetReference(Array.Empty<byte>()));
        Assert.Catch<ArgumentException>(() => new NativeBlobAssetReference(new byte[] { 1, 2, 3, 4 }, alignment: 3));
    }

    [Test]
    public unsafe void should_throw_when_blob_too_small_for_type()
    {
        using var reference = new NativeBlobAssetReference(new byte[] { 1, 2 });
        Assert.Catch<ArgumentException>(() => reference.GetUnsafePtr<int>());
    }
}
