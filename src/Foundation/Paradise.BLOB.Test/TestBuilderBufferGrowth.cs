namespace Paradise.BLOB.Test;

public class TestBuilderBufferGrowth
{
    [Test]
    [Arguments(0, 0)]
    [Arguments(1, 0)]
    [Arguments(8, 0)]
    [Arguments(4096, 4088)]
    public void should_patch_generic_any_pointer_after_buffer_growth(int capacity, int position)
    {
        AssertPointerAfterGrowth(new AnyPtrBuilder<long>(42), capacity, position);
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(1, 0)]
    [Arguments(8, 0)]
    [Arguments(4096, 4088)]
    public void should_patch_untyped_any_pointer_after_buffer_growth(int capacity, int position)
    {
        var builder = new AnyPtrBuilder();
        builder.SetValue(42L);
        AssertPointerAfterGrowth(builder, capacity, position);
    }

    private static unsafe void AssertPointerAfterGrowth(IBuilder<BlobPtrAny> builder, int capacity, int position)
    {
        using var stream = CreateStream(capacity, position);
        var originalBuffer = stream.Buffer;
        builder.Build(stream);

        Assert.AreEqual(false, ReferenceEquals(originalBuffer, stream.Buffer));
        Assert.AreEqual(position, builder.DataPosition);
        Assert.AreEqual(sizeof(BlobPtrAny), builder.DataSize);
        Assert.AreEqual(position + sizeof(BlobPtrAny), builder.PatchPosition);
        Assert.AreEqual(sizeof(long), builder.PatchSize);

        var expected = CreateExpectedBytes(position, 16);
        BitConverter.GetBytes(8).CopyTo(expected, position);
        BitConverter.GetBytes(sizeof(long)).CopyTo(expected, position + 4);
        BitConverter.GetBytes(42L).CopyTo(expected, position + 8);
        var bytes = stream.ToArray();
        Assert.AreEqual(expected, bytes);

        using var blob = new ManagedBlobAssetReference(bytes);
        ref var pointer = ref *(BlobPtrAny*)(blob.GetUnsafePtr<byte>() + position);
        Assert.AreEqual(sizeof(long), pointer.Size);
        Assert.AreEqual(42L, pointer.GetValue<long>());
    }

    [Test]
    [Arguments(16, 0)]
    [Arguments(4096, 4080)]
    public unsafe void should_patch_any_array_and_record_positions_after_buffer_growth(int capacity, int position)
    {
        var builder = new AnyArrayBuilder();
        builder.Add(42L);
        using var stream = CreateStream(capacity, position);
        var originalBuffer = stream.Buffer;
        builder.Build(stream);

        Assert.AreEqual(false, ReferenceEquals(originalBuffer, stream.Buffer));
        Assert.AreEqual(position, builder.DataPosition);
        Assert.AreEqual(sizeof(BlobArrayAny), builder.DataSize);
        Assert.AreEqual(position + 16, builder.PatchPosition);
        Assert.AreEqual(16, builder.PatchSize);
        Assert.AreEqual(position, builder.OffsetsBuilder.DataPosition);
        Assert.AreEqual(sizeof(BlobArray<int>), builder.OffsetsBuilder.DataSize);
        Assert.AreEqual(position + 16, builder.OffsetsBuilder.PatchPosition);
        Assert.AreEqual(8, builder.OffsetsBuilder.PatchSize);
        Assert.AreEqual(position + 8, builder.DataBuilder.DataPosition);
        Assert.AreEqual(sizeof(BlobArray<byte>), builder.DataBuilder.DataSize);
        Assert.AreEqual(position + 24, builder.DataBuilder.PatchPosition);
        Assert.AreEqual(sizeof(long), builder.DataBuilder.PatchSize);

        var expected = CreateExpectedBytes(position, 32);
        var headerAndOffsets = new[] { 16, 2, 16, 8, 0, 8 };
        for (var i = 0; i < headerAndOffsets.Length; i++)
        {
            BitConverter.GetBytes(headerAndOffsets[i]).CopyTo(expected, position + sizeof(int) * i);
        }
        BitConverter.GetBytes(42L).CopyTo(expected, position + 24);
        var bytes = stream.ToArray();
        Assert.AreEqual(expected, bytes);

        using var blob = new ManagedBlobAssetReference(bytes);
        ref var array = ref *(BlobArrayAny*)(blob.GetUnsafePtr<byte>() + position);
        Assert.AreEqual(1, array.Length);
        Assert.AreEqual(new[] { 0, 8 }, array.Offsets.ToArray());
        Assert.AreEqual(sizeof(long), array.Data.Length);
        Assert.AreEqual(sizeof(long), array.GetSize(0));
        Assert.AreEqual(42L, array.GetValue<long>(0));
    }

    [Test]
    public void should_record_empty_any_array_after_offsets_write_grows_buffer()
    {
        var builder = new AnyArrayBuilder();
        using var stream = new BlobMemoryStream(16);
        var originalBuffer = stream.Buffer;
        builder.Build(stream);

        Assert.AreEqual(false, ReferenceEquals(originalBuffer, stream.Buffer));
        Assert.AreEqual(20, stream.Length);
        Assert.AreEqual(4, builder.PatchSize);
        Assert.AreEqual(16, builder.OffsetsBuilder.PatchPosition);
        Assert.AreEqual(4, builder.OffsetsBuilder.PatchSize);
        Assert.AreEqual(20, builder.DataBuilder.PatchPosition);
        Assert.AreEqual(0, builder.DataBuilder.PatchSize);

        using var blob = new ManagedBlobAssetReference<BlobArrayAny>(stream.ToArray());
        Assert.AreEqual(0, blob.Value.Length);
        Assert.AreEqual(new[] { 0 }, blob.Value.Offsets.ToArray());
        Assert.AreEqual(0, blob.Value.Data.Length);
    }

    [Test]
    [Arguments(16)]
    [Arguments(4096)]
    public void should_preserve_nested_any_payloads_across_multiple_buffer_growths(int capacity)
    {
        var payload = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        var untypedPointer = new AnyPtrBuilder();
        untypedPointer.SetValue(84L);
        var inner = new AnyArrayBuilder();
        inner.Add(new AnyPtrBuilder<long>(42));
        inner.Add(untypedPointer);
        inner.Add(new ArrayBuilder<byte>(payload));
        var outer = new AnyArrayBuilder();
        outer.Add(inner);
        outer.Add(123);

        using var stream = new BlobMemoryStream(capacity);
        var originalBuffer = stream.Buffer;
        outer.Build(stream);
        Assert.AreEqual(false, ReferenceEquals(originalBuffer, stream.Buffer));
        var bytes = stream.ToArray();

        using var presized = new BlobMemoryStream(32768);
        var presizedBuffer = presized.Buffer;
        outer.Build(presized);
        Assert.AreEqual(true, ReferenceEquals(presizedBuffer, presized.Buffer));
        Assert.AreEqual(presized.ToArray(), bytes);

        using var blob = new ManagedBlobAssetReference<BlobArrayAny>(bytes);
        Assert.AreEqual(2, blob.Value.Length);
        Assert.AreEqual(outer.DataBuilder.PatchSize, blob.Value.Data.Length);
        Assert.AreEqual(bytes.Length, outer.DataBuilder.PatchPosition + blob.Value.Data.Length);
        ref var nested = ref blob.Value.GetValue<BlobArrayAny>(0);
        Assert.AreEqual(3, nested.Length);
        Assert.AreEqual(inner.DataBuilder.PatchSize, nested.Data.Length);
        Assert.AreEqual(nested.Data.Length, nested.Offsets[nested.Length]);
        Assert.AreEqual(sizeof(long), nested.GetValue<BlobPtrAny>(0).Size);
        Assert.AreEqual(42L, nested.GetValue<BlobPtrAny>(0).GetValue<long>());
        Assert.AreEqual(sizeof(long), nested.GetValue<BlobPtrAny>(1).Size);
        Assert.AreEqual(84L, nested.GetValue<BlobPtrAny>(1).GetValue<long>());
        Assert.AreEqual(payload, nested.GetValue<BlobArray<byte>>(2).ToArray());
        Assert.AreEqual(123, blob.Value.GetValue<int>(1));
    }

    private static BlobMemoryStream CreateStream(int capacity, int position)
    {
        var stream = new BlobMemoryStream(capacity) { Length = position, Position = position };
        stream.Buffer.AsSpan(0, position).Fill(0x5a);
        return stream;
    }

    private static byte[] CreateExpectedBytes(int position, int size)
    {
        var bytes = new byte[position + size];
        bytes.AsSpan(0, position).Fill(0x5a);
        return bytes;
    }
}
