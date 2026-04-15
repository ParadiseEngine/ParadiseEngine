using System;
using System.Text;

namespace Paradise.BLOB.Test;

using BlobString = BlobString<UTF8Encoding>;
using BlobStringBuilder = StringBuilder<UTF8Encoding>;

public class TestBlobMemoryStream
{
    [Test]
    public void should_have_default_capacity()
    {
        using var stream = new BlobMemoryStream();
        Assert.AreEqual(0, stream.Position);
        Assert.AreEqual(0, stream.Length);
    }

    [Test]
    public void should_accept_custom_capacity()
    {
        using var stream = new BlobMemoryStream(256);
        Assert.AreEqual(0, stream.Position);
        Assert.AreEqual(0, stream.Length);
    }

    [Test]
    public void should_set_and_get_position()
    {
        using var stream = new BlobMemoryStream();
        stream.Length = 100;
        stream.Position = 50;
        Assert.AreEqual(50, stream.Position);
    }

    [Test]
    public void should_set_and_get_length()
    {
        using var stream = new BlobMemoryStream();
        stream.Length = 200;
        Assert.AreEqual(200, stream.Length);
    }

    [Test]
    public void should_default_alignment_to_4()
    {
        using var stream = new BlobMemoryStream();
        Assert.AreEqual(4, stream.Alignment);
    }

    [Test]
    public void should_allow_setting_alignment()
    {
        using var stream = new BlobMemoryStream();
        stream.Alignment = 8;
        Assert.AreEqual(8, stream.Alignment);
    }

    [Test]
    public unsafe void should_write_and_read_back()
    {
        using var stream = new BlobMemoryStream();
        stream.Length = 16;
        int value = 12345;
        stream.Write((byte*)&value, sizeof(int), 4);
        var arr = stream.ToArray();
        Assert.AreEqual(16, arr.Length);
        fixed (byte* ptr = arr)
        {
            Assert.AreEqual(12345, *(int*)ptr);
        }
    }

    [Test]
    public void should_grow_capacity_on_write()
    {
        using var stream = new BlobMemoryStream(4);
        stream.EnsureDataSize(1024, 4);
        Assert.GreaterOrEqual(stream.Length, 1024);
    }

    [Test]
    public void should_return_correct_array()
    {
        using var stream = new BlobMemoryStream();
        stream.Length = 8;
        var arr = stream.ToArray();
        Assert.AreEqual(8, arr.Length);
    }

    [Test]
    public void should_provide_buffer()
    {
        using var stream = new BlobMemoryStream();
        stream.Length = 4;
        Assert.IsNotNull(stream.Buffer);
        Assert.GreaterOrEqual(stream.Buffer.Length, 4);
    }

    [Test]
    public void should_track_patch_position()
    {
        using var stream = new BlobMemoryStream();
        stream.PatchPosition = 16;
        Assert.AreEqual(16, stream.PatchPosition);
    }

    [Test]
    public void should_update_patch_position_on_write()
    {
        using var stream = new BlobMemoryStream();
        stream.EnsureDataSize(8, 4);
        Assert.GreaterOrEqual(stream.PatchPosition, 8);
    }
}

public class TestBlobStreamExtension
{
    [Test]
    public void should_ensure_data_size_and_expand()
    {
        using var stream = new BlobMemoryStream();
        stream.EnsureDataSize(32, 4);
        Assert.GreaterOrEqual(stream.Length, 32);
        Assert.GreaterOrEqual(stream.PatchPosition, 32);
    }

    [Test]
    public void should_ensure_data_size_generic()
    {
        using var stream = new BlobMemoryStream();
        stream.EnsureDataSize<long>();
        Assert.GreaterOrEqual(stream.Length, sizeof(long));
    }

    [Test]
    public void should_expand_patch()
    {
        using var stream = new BlobMemoryStream();
        stream.PatchPosition = 8;
        stream.ExpandPatch(16, 4);
        Assert.GreaterOrEqual(stream.PatchPosition, 24);
    }

    [Test]
    public void should_align_patch()
    {
        using var stream = new BlobMemoryStream();
        stream.PatchPosition = 5;
        stream.AlignPatch(4);
        Assert.AreEqual(8, stream.PatchPosition);
    }

    [Test]
    public unsafe void should_write_value_generic()
    {
        using var stream = new BlobMemoryStream();
        stream.Length = 16;
        stream.WriteValue(42);
        var arr = stream.ToArray();
        fixed (byte* ptr = arr)
        {
            Assert.AreEqual(42, *(int*)ptr);
        }
    }

    [Test]
    public void should_write_offset()
    {
        using var stream = new BlobMemoryStream();
        stream.Length = 16;
        stream.Position = 0;
        stream.WriteOffset(8);
        var arr = stream.ToArray();
        // Offset should be 8 - 0 = 8
        Assert.AreEqual(8, BitConverter.ToInt32(arr, 0));
    }

    [Test]
    public void should_calculate_offset()
    {
        using var stream = new BlobMemoryStream();
        stream.Position = 4;
        Assert.AreEqual(12, stream.Offset(16));
    }

    [Test]
    public void should_calculate_patch_offset()
    {
        using var stream = new BlobMemoryStream();
        stream.Position = 0;
        stream.PatchPosition = 20;
        Assert.AreEqual(20, stream.PatchOffset());
    }

    [Test]
    public void should_to_position()
    {
        using var stream = new BlobMemoryStream();
        stream.Length = 100;
        stream.ToPosition(50);
        Assert.AreEqual(50, stream.Position);
    }

    [Test]
    public void should_to_patch_position()
    {
        using var stream = new BlobMemoryStream();
        stream.PatchPosition = 32;
        stream.Length = 64;
        stream.ToPatchPosition();
        Assert.AreEqual(32, stream.Position);
    }

    [Test]
    public void should_get_alignment_with_positive_value()
    {
        using var stream = new BlobMemoryStream();
        stream.Alignment = 4;
        Assert.AreEqual(8, stream.GetAlignment(8));
    }

    [Test]
    public void should_get_alignment_with_zero_uses_stream_default()
    {
        using var stream = new BlobMemoryStream();
        stream.Alignment = 4;
        Assert.AreEqual(4, stream.GetAlignment(0));
    }
}

public class TestValueBuilder
{
    [Test]
    public void should_build_default_value()
    {
        var builder = new ValueBuilder<int>();
        Assert.AreEqual(0, builder.Value);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(0, blob.Value);
    }

    [Test]
    public void should_build_specified_value()
    {
        var builder = new ValueBuilder<int>(42);
        Assert.AreEqual(42, builder.Value);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(42, blob.Value);
    }

    [Test]
    public void should_allow_modifying_value_before_build()
    {
        var builder = new ValueBuilder<int>(10);
        builder.Value = 99;
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(99, blob.Value);
    }

    [Test]
    public void should_build_float_value()
    {
        var builder = new ValueBuilder<float>(3.14f);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(3.14f, blob.Value);
    }

    [Test]
    public void should_build_long_value()
    {
        var builder = new ValueBuilder<long>(long.MaxValue);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(long.MaxValue, blob.Value);
    }

    [Test]
    public void should_build_byte_value()
    {
        var builder = new ValueBuilder<byte>(255);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(255, blob.Value);
    }

    struct TestStruct
    {
        public int X;
        public float Y;
        public byte Z;
    }

    [Test]
    public void should_build_struct_value()
    {
        var builder = new ValueBuilder<TestStruct>(new TestStruct { X = 1, Y = 2.5f, Z = 3 });
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(1, blob.Value.X);
        Assert.AreEqual(2.5f, blob.Value.Y);
        Assert.AreEqual(3, blob.Value.Z);
    }

    [Test]
    public void should_track_data_position_and_size()
    {
        var builder = new ValueBuilder<int>(42);
        using var stream = new BlobMemoryStream();
        builder.Build(stream);
        Assert.AreEqual(0, builder.DataPosition);
        Assert.AreEqual(sizeof(int), builder.DataSize);
    }
}

public class TestStructBuilder
{
    struct SimpleStruct
    {
        public int A;
        public float B;
    }

    [Test]
    public void should_build_struct_with_set_values()
    {
        var builder = new StructBuilder<SimpleStruct>();
        builder.SetValue(ref builder.Value.A, 42);
        builder.SetValue(ref builder.Value.B, 3.14f);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(42, blob.Value.A);
        Assert.AreEqual(3.14f, blob.Value.B);
    }

    [Test]
    public void should_get_builder_for_field()
    {
        var builder = new StructBuilder<SimpleStruct>();
        builder.SetValue(ref builder.Value.A, 42);
        var fieldBuilder = builder.GetBuilder(ref builder.Value.A);
        Assert.IsNotNull(fieldBuilder);
    }

    struct StructWithArray
    {
        public int Value;
        public BlobArray<int> Array;
    }

    [Test]
    public void should_build_struct_with_array()
    {
        var builder = new StructBuilder<StructWithArray>();
        builder.SetValue(ref builder.Value.Value, 100);
        builder.SetArray(ref builder.Value.Array, new[] { 1, 2, 3 });
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(100, blob.Value.Value);
        Assert.AreEqual(3, blob.Value.Array.Length);
        Assert.That(blob.Value.Array.ToArray(), Is.EquivalentTo(new[] { 1, 2, 3 }));
    }

    struct StructWithPtr
    {
        public int Value;
        public BlobPtr<int> Ptr;
    }

    [Test]
    public void should_build_struct_with_pointer_to_field()
    {
        var builder = new StructBuilder<StructWithPtr>();
        var valueBuilder = builder.SetValue(ref builder.Value.Value, 42);
        builder.SetPointer(ref builder.Value.Ptr, valueBuilder);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(42, blob.Value.Value);
        Assert.AreEqual(42, blob.Value.Ptr.Value);
    }

    [Test]
    public void should_build_struct_with_pointer_to_new_value()
    {
        var builder = new StructBuilder<StructWithPtr>();
        builder.SetValue(ref builder.Value.Value, 10);
        builder.SetPointer(ref builder.Value.Ptr, 99);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(10, blob.Value.Value);
        Assert.AreEqual(99, blob.Value.Ptr.Value);
    }

    struct StructWithString
    {
        public BlobString String;
        public int Value;
    }

    [Test]
    public void should_build_struct_with_string()
    {
        var builder = new StructBuilder<StructWithString>();
        builder.SetString(ref builder.Value.String, "Hello");
        builder.SetValue(ref builder.Value.Value, 42);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual("Hello", blob.Value.String.ToString());
        Assert.AreEqual(42, blob.Value.Value);
    }

    [Test]
    public void should_override_field_with_later_set()
    {
        var builder = new StructBuilder<SimpleStruct>();
        builder.SetValue(ref builder.Value.A, 10);
        builder.SetValue(ref builder.Value.A, 20);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(20, blob.Value.A);
    }

    struct StructWithArrayBuilders
    {
        public BlobArray<BlobPtr<int>> PtrArray;
    }

    [Test]
    public void should_build_struct_with_array_of_builders()
    {
        var builder = new StructBuilder<StructWithArrayBuilders>();
        var itemBuilders = new IBuilder<BlobPtr<int>>[]
        {
            new PtrBuilderWithNewValue<int>(10),
            new PtrBuilderWithNewValue<int>(20),
            new PtrBuilderWithNewValue<int>(30),
        };
        builder.SetArray(ref builder.Value.PtrArray, itemBuilders);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(3, blob.Value.PtrArray.Length);
        Assert.AreEqual(10, blob.Value.PtrArray[0].Value);
        Assert.AreEqual(20, blob.Value.PtrArray[1].Value);
        Assert.AreEqual(30, blob.Value.PtrArray[2].Value);
    }
}

public class TestArrayBuilderVariants
{
    [Test]
    public void should_build_from_enumerable()
    {
        IEnumerable<int> items = new List<int> { 1, 2, 3 };
        var builder = new ArrayBuilder<int>(items);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(3, blob.Value.Length);
        Assert.That(blob.Value.ToArray(), Is.EquivalentTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void should_build_empty_array()
    {
        var builder = new ArrayBuilder<int>();
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(0, blob.Value.Length);
    }

    [Test]
    public void should_build_with_item_builders()
    {
        var builders = new IBuilder<BlobPtr<int>>[]
        {
            new PtrBuilderWithNewValue<int>(100),
            new PtrBuilderWithNewValue<int>(200),
        };
        var builder = new ArrayBuilderWithItemBuilders<BlobPtr<int>>(builders);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(2, blob.Value.Length);
        Assert.AreEqual(100, blob.Value[0].Value);
        Assert.AreEqual(200, blob.Value[1].Value);
    }

    [Test]
    public void should_access_item_builders_by_index()
    {
        var builders = new IBuilder<int>[]
        {
            new ValueBuilder<int>(10),
            new ValueBuilder<int>(20),
        };
        var builder = new ArrayBuilderWithItemBuilders<int>(builders);
        Assert.IsNotNull(builder[0]);
        Assert.IsNotNull(builder[1]);
    }

    [Test]
    public void should_build_with_item_position()
    {
        var builder = new ArrayBuilderWithItemPosition<float>(new[] { 1f, 2f, 3f });
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(3, blob.Value.Length);
        Assert.AreEqual(1f, blob.Value[0]);
        Assert.AreEqual(2f, blob.Value[1]);
        Assert.AreEqual(3f, blob.Value[2]);
    }

    [Test]
    public void should_track_item_positions()
    {
        var builder = new ArrayBuilderWithItemPosition<int>(new[] { 10, 20, 30 });
        var blob = builder.CreateManagedBlobAssetReference();
        // After build, item position builders should have DataPosition set
        var itemBuilder = builder[0];
        Assert.GreaterOrEqual(itemBuilder.DataPosition, 0);
        Assert.AreEqual(sizeof(int), itemBuilder.DataSize);
    }
}

public class TestPtrBuilderVariants
{
    [Test]
    public void should_build_ptr_with_default_value()
    {
        var builder = new PtrBuilderWithNewValue<int>();
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(0, blob.Value.Value);
    }

    [Test]
    public void should_build_ptr_with_specified_value()
    {
        var builder = new PtrBuilderWithNewValue<int>(42);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(42, blob.Value.Value);
    }

    [Test]
    public void should_build_ptr_with_builder()
    {
        var valueBuilder = new ValueBuilder<int>(99);
        var builder = new PtrBuilderWithNewValue<int>(valueBuilder);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(99, blob.Value.Value);
    }

    [Test]
    public void should_expose_value_builder()
    {
        var valueBuilder = new ValueBuilder<int>(42);
        var builder = new PtrBuilderWithNewValue<int>(valueBuilder);
        Assert.IsNotNull(builder.ValueBuilder);
    }

    [Test]
    public void should_build_ref_ptr()
    {
        var structBuilder = new ValueBuilder<int>(100);
        var ptrBuilder = new PtrBuilderWithRefBuilder<int>(structBuilder);
        // PtrBuilderWithRefBuilder references existing data position
        var blob = ptrBuilder.CreateManagedBlobAssetReference();
        // This tests the pointer resolution mechanics
        Assert.IsNotNull(blob);
    }

    [Test]
    public void should_expose_ref_value_builder()
    {
        var valueBuilder = new ValueBuilder<int>(42);
        var builder = new PtrBuilderWithRefBuilder<int>(valueBuilder);
        Assert.IsNotNull(builder.ValueBuilder);
    }
}

public class TestValuePositionBuilder
{
    [Test]
    public void should_store_data_position()
    {
        var builder = new ValuePositionBuilder();
        builder.DataPosition = 16;
        Assert.AreEqual(16, builder.DataPosition);
    }

    [Test]
    public void should_store_data_size()
    {
        var builder = new ValuePositionBuilder();
        builder.DataSize = 8;
        Assert.AreEqual(8, builder.DataSize);
    }

    [Test]
    public void should_store_patch_position()
    {
        var builder = new ValuePositionBuilder();
        builder.PatchPosition = 32;
        Assert.AreEqual(32, builder.PatchPosition);
    }

    [Test]
    public void should_store_patch_size()
    {
        var builder = new ValuePositionBuilder();
        builder.PatchSize = 16;
        Assert.AreEqual(16, builder.PatchSize);
    }

    [Test]
    public void should_throw_on_build()
    {
        var builder = new ValuePositionBuilder();
        using var stream = new BlobMemoryStream();
        Assert.Catch<NotSupportedException>(() => builder.Build(stream));
    }

    [Test]
    public void should_work_as_generic_variant()
    {
        var builder = new ValuePositionBuilder<int>();
        builder.DataPosition = 8;
        Assert.AreEqual(8, builder.DataPosition);
    }
}

public class TestUnsafeBlobStreamValue
{
    [Test]
    public unsafe void should_read_value_from_stream()
    {
        using var stream = new BlobMemoryStream();
        stream.Length = 16;
        int value = 42;
        stream.Write((byte*)&value, sizeof(int), 4);

        var unsafeValue = new UnsafeBlobStreamValue<int>(stream, 0);
        Assert.AreEqual(42, unsafeValue.Value);
    }

    [Test]
    public void should_throw_on_invalid_position()
    {
        using var stream = new BlobMemoryStream();
        stream.Length = 2;
        Assert.Catch<ArgumentException>(() =>
        {
            var _ = new UnsafeBlobStreamValue<int>(stream, 0);
        });
    }

    [Test]
    public unsafe void should_write_via_ref()
    {
        using var stream = new BlobMemoryStream();
        stream.Length = 16;
        // Initialize with zero
        int zero = 0;
        stream.Write((byte*)&zero, sizeof(int), 4);

        var unsafeValue = new UnsafeBlobStreamValue<int>(stream, 0);
        unsafeValue.Value = 99;
        Assert.AreEqual(99, unsafeValue.Value);
    }
}

public class TestBuilderExtension
{
    [Test]
    public void should_create_blob_bytes()
    {
        var builder = new ValueBuilder<int>(42);
        var blob = builder.CreateBlob();
        Assert.IsNotNull(blob);
        Assert.GreaterOrEqual(blob.Length, sizeof(int));
    }

    [Test]
    public void should_create_managed_blob_asset_reference_generic()
    {
        var builder = new ValueBuilder<int>(42);
        using var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(42, blob.Value);
    }

    [Test]
    public void should_create_managed_blob_asset_reference_non_generic()
    {
        IBuilder builder = new ValueBuilder<int>(42);
        using var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(42, blob.GetValue<int>());
    }

    struct StructWithBlobPtrAny
    {
        public BlobPtrAny Ptr;
        public int Value;
    }

    [Test]
    public void should_set_pointer_any()
    {
        var builder = new StructBuilder<StructWithBlobPtrAny>();
        builder.SetPointerAny(ref builder.Value.Ptr, 42);
        builder.SetValue(ref builder.Value.Value, 100);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(42, blob.Value.Ptr.GetValue<int>());
        Assert.AreEqual(100, blob.Value.Value);
    }
}

public class TestManagedBlobAssetReferenceEdgeCases
{
    [Test]
    public void should_expose_blob_bytes()
    {
        var builder = new ValueBuilder<int>(42);
        using var blob = builder.CreateManagedBlobAssetReference();
        Assert.IsNotNull(blob.Blob);
        Assert.GreaterOrEqual(blob.Blob.Length, sizeof(int));
    }

    [Test]
    public void should_expose_length()
    {
        var builder = new ValueBuilder<int>(42);
        using var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(blob.Blob.Length, blob.Length);
    }

    [Test]
    public void should_reject_empty_blob_generic()
    {
        Assert.Catch<ArgumentException>(() => new ManagedBlobAssetReference<int>(Array.Empty<byte>()));
    }

    [Test]
    public void should_reject_empty_blob_non_generic()
    {
        Assert.Catch<ArgumentException>(() => new ManagedBlobAssetReference(Array.Empty<byte>()));
    }

    [Test]
    public void should_not_throw_on_double_dispose_generic()
    {
        var builder = new ValueBuilder<int>(42);
        var blob = builder.CreateManagedBlobAssetReference();
        blob.Dispose();
        blob.Dispose();
    }

    [Test]
    public void should_not_throw_on_double_dispose_non_generic()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var blob = new ManagedBlobAssetReference(bytes);
        blob.Dispose();
        blob.Dispose();
    }

    [Test]
    public unsafe void should_expose_unsafe_ptr_generic()
    {
        var builder = new ValueBuilder<int>(42);
        using var blob = builder.CreateManagedBlobAssetReference();
        var ptr = blob.UnsafePtr;
        Assert.AreEqual(42, *ptr);
    }

    [Test]
    public unsafe void should_expose_stable_unsafe_ptr_generic()
    {
        var builder = new ValueBuilder<int>(42);
        using var blob = builder.CreateManagedBlobAssetReference();
        var ptr1 = blob.UnsafePtr;
        var ptr2 = blob.UnsafePtr;
        Assert.AreEqual((long)ptr1, (long)ptr2);
    }

    [Test]
    public unsafe void non_generic_should_throw_when_too_small()
    {
        var bytes = new byte[] { 1, 2 };
        using var blob = new ManagedBlobAssetReference(bytes);
        Assert.Catch<ArgumentException>(() => blob.GetUnsafePtr<int>());
    }

    [Test]
    public unsafe void non_generic_should_return_valid_value()
    {
        var builder = new ValueBuilder<int>(42);
        var blobBytes = builder.CreateBlob();
        using var blob = new ManagedBlobAssetReference(blobBytes);
        ref var value = ref blob.GetValue<int>();
        Assert.AreEqual(42, value);
    }
}

public class TestTreeExtension
{
    sealed class SimpleNode
    {
        public int Value;
        public List<SimpleNode> Children = new List<SimpleNode>();
    }

    [Test]
    public void should_enumerate_self_and_descendants()
    {
        var root = new SimpleNode { Value = 1 };
        var child1 = new SimpleNode { Value = 2 };
        var child2 = new SimpleNode { Value = 3 };
        var grandchild = new SimpleNode { Value = 4 };
        root.Children.Add(child1);
        root.Children.Add(child2);
        child1.Children.Add(grandchild);

        var all = root.SelfAndDescendants(n => n.Children).ToArray();
        Assert.AreEqual(4, all.Length);
        Assert.AreEqual(1, all[0].Value);
        Assert.AreEqual(2, all[1].Value);
        Assert.AreEqual(4, all[2].Value);
        Assert.AreEqual(3, all[3].Value);
    }

    [Test]
    public void should_handle_leaf_node()
    {
        var leaf = new SimpleNode { Value = 1 };
        var all = leaf.SelfAndDescendants(n => n.Children).ToArray();
        Assert.AreEqual(1, all.Length);
        Assert.AreEqual(1, all[0].Value);
    }
}
