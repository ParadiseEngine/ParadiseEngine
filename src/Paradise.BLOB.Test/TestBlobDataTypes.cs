using System;
using System.Text;

namespace Paradise.BLOB.Test;

using BlobString = BlobString<UTF8Encoding>;
using BlobStringBuilder = StringBuilder<UTF8Encoding>;

public class TestBlobArray
{
    [Test]
    public void should_create_empty_array()
    {
        var builder = new ArrayBuilder<int>();
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(0, blob.Value.Length);
        Assert.That(blob.Value.ToArray(), Is.EquivalentTo(Array.Empty<int>()));
    }

    [Test]
    public void should_create_single_element_array()
    {
        var builder = new ArrayBuilder<int>(new[] { 42 });
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(1, blob.Value.Length);
        Assert.AreEqual(42, blob.Value[0]);
    }

    [Test]
    public void should_access_elements_by_indexer()
    {
        var builder = new ArrayBuilder<int>(new[] { 10, 20, 30, 40, 50 });
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(5, blob.Value.Length);
        Assert.AreEqual(10, blob.Value[0]);
        Assert.AreEqual(20, blob.Value[1]);
        Assert.AreEqual(30, blob.Value[2]);
        Assert.AreEqual(40, blob.Value[3]);
        Assert.AreEqual(50, blob.Value[4]);
    }

    [Test]
    public void should_throw_on_negative_index()
    {
        var builder = new ArrayBuilder<int>(new[] { 1, 2, 3 });
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.Catch<ArgumentOutOfRangeException>(() =>
        {
            ref var _ = ref blob.Value[-1];
        });
    }

    [Test]
    public void should_throw_on_index_equal_to_length()
    {
        var builder = new ArrayBuilder<int>(new[] { 1, 2, 3 });
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.Catch<ArgumentOutOfRangeException>(() =>
        {
            ref var _ = ref blob.Value[3];
        });
    }

    [Test]
    public void should_throw_on_index_greater_than_length()
    {
        var builder = new ArrayBuilder<int>(new[] { 1, 2, 3 });
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.Catch<ArgumentOutOfRangeException>(() =>
        {
            ref var _ = ref blob.Value[100];
        });
    }

    [Test]
    public void should_convert_to_array()
    {
        var expected = new long[] { 100, 200, 300, 400 };
        var builder = new ArrayBuilder<long>(expected);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.That(blob.Value.ToArray(), Is.EquivalentTo(expected));
    }

    [Test]
    public void should_convert_empty_to_array()
    {
        var builder = new ArrayBuilder<int>();
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.That(blob.Value.ToArray(), Is.EquivalentTo(Array.Empty<int>()));
    }

    [Test]
    public void should_convert_to_span()
    {
        var expected = new int[] { 1, 2, 3, 4, 5 };
        var builder = new ArrayBuilder<int>(expected);
        var blob = builder.CreateManagedBlobAssetReference();
        var span = blob.Value.ToSpan();
        Assert.AreEqual(expected.Length, span.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.AreEqual(expected[i], span[i]);
    }

    [Test]
    public unsafe void should_provide_unsafe_ptr()
    {
        var builder = new ArrayBuilder<int>(new[] { 10, 20, 30 });
        var blob = builder.CreateManagedBlobAssetReference();
        var ptr = blob.Value.UnsafePtr;
        Assert.AreEqual(10, ptr[0]);
        Assert.AreEqual(20, ptr[1]);
        Assert.AreEqual(30, ptr[2]);
    }

    [Test]
    public void should_work_with_byte_arrays()
    {
        var expected = new byte[] { 0, 1, 127, 255 };
        var builder = new ArrayBuilder<byte>(expected);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(expected.Length, blob.Value.Length);
        Assert.That(blob.Value.ToArray(), Is.EquivalentTo(expected));
    }

    [Test]
    public void should_work_with_double_arrays()
    {
        var expected = new[] { Math.PI, Math.E, double.MaxValue, double.MinValue, 0.0 };
        var builder = new ArrayBuilder<double>(expected);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(expected.Length, blob.Value.Length);
        Assert.That(blob.Value.ToArray(), Is.EquivalentTo(expected));
    }

    [Test]
    public void should_return_ref_from_indexer()
    {
        var builder = new ArrayBuilder<int>(new[] { 10, 20, 30 });
        var blob = builder.CreateManagedBlobAssetReference();
        ref var second = ref blob.Value[1];
        Assert.AreEqual(20, second);
        // Modify via ref
        second = 99;
        Assert.AreEqual(99, blob.Value[1]);
    }
}

public class TestBlobString
{
    [Test]
    public void should_create_empty_string()
    {
        var builder = new BlobStringBuilder("");
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual("", blob.Value.ToString());
        Assert.AreEqual(0, blob.Value.Length);
    }

    [Test]
    public void should_create_ascii_string()
    {
        var builder = new BlobStringBuilder("Hello World");
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual("Hello World", blob.Value.ToString());
        Assert.AreEqual(new UTF8Encoding().GetByteCount("Hello World"), blob.Value.Length);
    }

    [Test]
    public void should_create_unicode_string()
    {
        const string text = "こんにちは世界";
        var builder = new BlobStringBuilder(text);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(text, blob.Value.ToString());
    }

    [Test]
    public void should_create_chinese_string()
    {
        const string text = "放大镜考过托福热情";
        var builder = new BlobStringBuilder(text);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(text, blob.Value.ToString());
    }

    [Test]
    public void should_roundtrip_with_unicode_encoding()
    {
        const string text = "Hello Unicode 日本語";
        var builder = new StringBuilder<UnicodeEncoding>(text);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(text, blob.Value.ToString());
        Assert.AreEqual(new UnicodeEncoding().GetByteCount(text), blob.Value.Length);
    }

    [Test]
    public void should_roundtrip_with_utf32_encoding()
    {
        const string text = "UTF-32 test 测试";
        var builder = new StringBuilder<UTF32Encoding>(text);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(text, blob.Value.ToString());
        Assert.AreEqual(new UTF32Encoding().GetByteCount(text), blob.Value.Length);
    }

    [Test]
    public void should_provide_span()
    {
        const string text = "Span test";
        var builder = new BlobStringBuilder(text);
        var blob = builder.CreateManagedBlobAssetReference();
        var span = blob.Value.ToSpan();
        var expected = new UTF8Encoding().GetBytes(text);
        Assert.AreEqual(expected.Length, span.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.AreEqual(expected[i], span[i]);
    }

    [Test]
    public unsafe void should_provide_unsafe_ptr()
    {
        const string text = "ABC";
        var builder = new BlobStringBuilder(text);
        var blob = builder.CreateManagedBlobAssetReference();
        var ptr = blob.Value.UnsafePtr;
        Assert.AreEqual((byte)'A', ptr[0]);
        Assert.AreEqual((byte)'B', ptr[1]);
        Assert.AreEqual((byte)'C', ptr[2]);
    }

    [Test]
    public void should_handle_default_builder()
    {
        var builder = new BlobStringBuilder();
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual("", blob.Value.ToString());
        Assert.AreEqual(0, blob.Value.Length);
    }
}

public class TestBlobNullTerminatedString
{
    [Test]
    public void should_create_empty_null_terminated_string()
    {
        var builder = new NullTerminatedStringBuilder<UTF8Encoding>("");
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual("", blob.Value.ToString());
        Assert.AreEqual(0, blob.Value.Length);
    }

    [Test]
    public void should_create_ascii_null_terminated_string()
    {
        const string text = "Hello";
        var builder = new NullTerminatedStringBuilder<UTF8Encoding>(text);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(text, blob.Value.ToString());
        // Length should be the byte count without null terminator
        Assert.AreEqual(new UTF8Encoding().GetByteCount(text), blob.Value.Length);
    }

    [Test]
    public void should_create_unicode_null_terminated_string()
    {
        const string text = "日本語テスト";
        var builder = new NullTerminatedStringBuilder<UTF8Encoding>(text);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(text, blob.Value.ToString());
    }

    [Test]
    public unsafe void should_have_null_terminator_in_data()
    {
        const string text = "ABC";
        var builder = new NullTerminatedStringBuilder<UTF8Encoding>(text);
        var blob = builder.CreateManagedBlobAssetReference();
        // Length property should exclude the null terminator
        Assert.AreEqual(3, blob.Value.Length);
        // Verify the null byte actually exists at position Length
        Assert.AreEqual(0, blob.Value.UnsafePtr[blob.Value.Length]);
    }

    [Test]
    public void should_roundtrip_with_unicode_encoding()
    {
        const string text = "Unicode null-terminated 测试";
        var builder = new NullTerminatedStringBuilder<UnicodeEncoding>(text);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(text, blob.Value.ToString());
    }

    [Test]
    public void should_provide_span()
    {
        const string text = "Span test";
        var builder = new NullTerminatedStringBuilder<UTF8Encoding>(text);
        var blob = builder.CreateManagedBlobAssetReference();
        var span = blob.Value.ToSpan();
        var expected = new UTF8Encoding().GetBytes(text);
        Assert.AreEqual(expected.Length, span.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.AreEqual(expected[i], span[i]);
    }

    [Test]
    public unsafe void should_provide_unsafe_ptr()
    {
        const string text = "XYZ";
        var builder = new NullTerminatedStringBuilder<UTF8Encoding>(text);
        var blob = builder.CreateManagedBlobAssetReference();
        var ptr = blob.Value.UnsafePtr;
        Assert.AreEqual((byte)'X', ptr[0]);
        Assert.AreEqual((byte)'Y', ptr[1]);
        Assert.AreEqual((byte)'Z', ptr[2]);
    }

    [Test]
    public void should_handle_default_builder()
    {
        var builder = new NullTerminatedStringBuilder<UTF8Encoding>();
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual("", blob.Value.ToString());
        Assert.AreEqual(0, blob.Value.Length);
    }
}

public class TestBlobPtr
{
    [Test]
    public void should_dereference_int_value()
    {
        var builder = new PtrBuilderWithNewValue<int>(42);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(42, blob.Value.Value);
    }

    [Test]
    public void should_dereference_long_value()
    {
        var builder = new PtrBuilderWithNewValue<long>(long.MaxValue);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(long.MaxValue, blob.Value.Value);
    }

    [Test]
    public void should_dereference_double_value()
    {
        var builder = new PtrBuilderWithNewValue<double>(Math.PI);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(Math.PI, blob.Value.Value);
    }

    [Test]
    public void should_dereference_byte_value()
    {
        var builder = new PtrBuilderWithNewValue<byte>(255);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(255, blob.Value.Value);
    }

    [Test]
    public unsafe void should_provide_unsafe_ptr()
    {
        var builder = new PtrBuilderWithNewValue<int>(99);
        var blob = builder.CreateManagedBlobAssetReference();
        var ptr = blob.Value.UnsafePtr;
        Assert.AreEqual(99, *ptr);
    }

    [Test]
    public void should_return_ref_from_value()
    {
        var builder = new PtrBuilderWithNewValue<int>(50);
        var blob = builder.CreateManagedBlobAssetReference();
        ref var val = ref blob.Value.Value;
        Assert.AreEqual(50, val);
        // Modify via ref
        val = 100;
        Assert.AreEqual(100, blob.Value.Value);
    }

    struct SimpleStruct
    {
        public int A;
        public float B;
    }

    [Test]
    public void should_dereference_struct_value()
    {
        var builder = new PtrBuilderWithNewValue<SimpleStruct>(new SimpleStruct { A = 10, B = 3.14f });
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(10, blob.Value.Value.A);
        Assert.AreEqual(3.14f, blob.Value.Value.B);
    }

    struct StructWithSelfPtr
    {
        public int Value;
        public BlobPtr<int> Ptr;
    }

    [Test]
    public void should_work_with_ref_builder_pointing_to_struct_field()
    {
        var builder = new StructBuilder<StructWithSelfPtr>();
        var valueBuilder = builder.SetValue(ref builder.Value.Value, 42);
        builder.SetPointer(ref builder.Value.Ptr, valueBuilder);

        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(42, blob.Value.Value);
        Assert.AreEqual(42, blob.Value.Ptr.Value);
    }

    [Test]
    public void should_dereference_ptr_to_ptr()
    {
        var innerBuilder = new PtrBuilderWithNewValue<int>(777);
        var outerBuilder = new PtrBuilderWithNewValue<BlobPtr<int>>(innerBuilder);
        var blob = outerBuilder.CreateManagedBlobAssetReference();
        Assert.AreEqual(777, blob.Value.Value.Value);
    }

    struct StructWithPtrToArray
    {
        public BlobArray<int> Array;
        public BlobPtr<BlobArray<int>> Ptr;
    }

    [Test]
    public void should_dereference_ptr_to_array()
    {
        var builder = new StructBuilder<StructWithPtrToArray>();
        var arrayBuilder = builder.SetArray(ref builder.Value.Array, new[] { 10, 20, 30 });
        builder.SetPointer(ref builder.Value.Ptr, arrayBuilder);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(3, blob.Value.Ptr.Value.Length);
        Assert.That(blob.Value.Ptr.Value.ToArray(), Is.EquivalentTo(new[] { 10, 20, 30 }));
    }

    [Test]
    public void should_dereference_ptr_to_string()
    {
        const string text = "Hello Ptr";
        var stringBuilder = new BlobStringBuilder(text);
        var ptrBuilder = new PtrBuilderWithNewValue<BlobString>(stringBuilder);
        var blob = ptrBuilder.CreateManagedBlobAssetReference();
        Assert.AreEqual(text, blob.Value.Value.ToString());
    }
}

public class TestBlobPtrAny
{
    [Test]
    public void should_get_int_value()
    {
        var builder = new AnyPtrBuilder<int>(42);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(42, blob.Value.GetValue<int>());
        Assert.AreEqual(sizeof(int), blob.Value.Size);
    }

    [Test]
    public void should_get_long_value()
    {
        var builder = new AnyPtrBuilder<long>(long.MaxValue);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(long.MaxValue, blob.Value.GetValue<long>());
        Assert.AreEqual(sizeof(long), blob.Value.Size);
    }

    [Test]
    public unsafe void should_throw_on_wrong_type_size()
    {
        var builder = new AnyPtrBuilder<int>(42);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.Catch<ArgumentException>(() =>
        {
            blob.Value.GetUnsafeValuePtr<long>();
        });
    }

    [Test]
    public void should_update_value_and_rebuild()
    {
        var builder = new AnyPtrBuilder<int>(100);
        builder.SetValue(200);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(200, blob.Value.GetValue<int>());
    }

    [Test]
    public void should_change_type_via_any_builder()
    {
        var builder = new AnyPtrBuilder();
        builder.SetValue(42);
        var blob1 = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(42, blob1.Value.GetValue<int>());
        Assert.AreEqual(sizeof(int), blob1.Value.Size);

        builder.SetValue(long.MaxValue);
        var blob2 = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(long.MaxValue, blob2.Value.GetValue<long>());
        Assert.AreEqual(sizeof(long), blob2.Value.Size);
    }

    [Test]
    public unsafe void should_provide_unsafe_ptr()
    {
        var builder = new AnyPtrBuilder<int>(99);
        var blob = builder.CreateManagedBlobAssetReference();
        var ptr = blob.Value.UnsafePtr;
        Assert.AreEqual(99, *(int*)ptr);
    }
}

public class TestBlobArrayAny
{
    [Test]
    public void should_get_values_of_different_types()
    {
        var builder = new AnyArrayBuilder();
        builder.Add(42);
        builder.Add(3.14);
        builder.Add(100L);

        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(3, blob.Value.Length);
        Assert.AreEqual(42, blob.Value.GetValue<int>(0));
        Assert.AreEqual(3.14, blob.Value.GetValue<double>(1));
        Assert.AreEqual(100L, blob.Value.GetValue<long>(2));
    }

    [Test]
    public void should_get_sizes_of_different_types()
    {
        var builder = new AnyArrayBuilder();
        builder.Add(42);
        builder.Add(3.14);

        var blob = builder.CreateManagedBlobAssetReference();
        // Size includes alignment padding between items
        Assert.GreaterOrEqual(blob.Value.GetSize(0), sizeof(int));
        Assert.GreaterOrEqual(blob.Value.GetSize(1), sizeof(double));
    }

    [Test]
    public void should_get_offsets()
    {
        var builder = new AnyArrayBuilder();
        builder.Add(42);
        builder.Add(100L);

        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(0, blob.Value.GetOffset(0));
    }

    [Test]
    public unsafe void should_throw_on_wrong_type_size()
    {
        var builder = new AnyArrayBuilder();
        builder.Add(42);

        var blob = builder.CreateManagedBlobAssetReference();
        Assert.Catch<ArgumentException>(() =>
        {
            blob.Value.GetUnsafeValuePtr<long>(0);
        });
    }

    [Test]
    public void should_handle_complex_items()
    {
        var builder = new AnyArrayBuilder();
        builder.Add(new PtrBuilderWithNewValue<int>(999));
        builder.Add(new ArrayBuilder<int>(new[] { 1, 2, 3 }));

        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(2, blob.Value.Length);
        Assert.AreEqual(999, blob.Value.GetValue<BlobPtr<int>>(0).Value);
        Assert.That(blob.Value.GetValue<BlobArray<int>>(1).ToArray(), Is.EquivalentTo(new[] { 1, 2, 3 }));
    }
}

public class TestBlobSortedArrayEdgeCases
{
    [Test]
    public void should_return_negative_index_for_missing_key()
    {
        var map = new Dictionary<int, int>
        {
            { 1, 100 },
            { 2, 200 },
            { 3, 300 },
        };
        var builder = new SortedArrayBuilder<int, int>(map);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(-1, blob.Value.IndexOfKey(999));
    }

    [Test]
    public void should_find_key_by_index()
    {
        var map = new Dictionary<int, int>
        {
            { 10, 100 },
            { 20, 200 },
            { 30, 300 },
        };
        var builder = new SortedArrayBuilder<int, int>(map);
        var blob = builder.CreateManagedBlobAssetReference();
        foreach (var pair in map)
        {
            var index = blob.Value.IndexOfKey(pair.Key);
            Assert.GreaterOrEqual(index, 0, $"Key {pair.Key} should be found");
        }
    }

    [Test]
    public void should_throw_on_missing_key_via_indexer()
    {
        var map = new Dictionary<int, int> { { 1, 100 } };
        var builder = new SortedArrayBuilder<int, int>(map);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.Catch<ArgumentException>(() =>
        {
            ref var _ = ref blob.Value[999];
        });
    }

    [Test]
    public void should_create_from_enumerable_of_tuples()
    {
        var items = new (int key, int value)[] { (1, 10), (2, 20), (3, 30) };
        var builder = new SortedArrayBuilder<int, int>(items);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(3, blob.Value.Length);
        Assert.AreEqual(10, blob.Value[1]);
        Assert.AreEqual(20, blob.Value[2]);
        Assert.AreEqual(30, blob.Value[3]);
    }

    [Test]
    public void should_create_from_builder_tuples()
    {
        var items = new (int key, IBuilder<int> builder)[]
        {
            (1, new ValueBuilder<int>(10)),
            (2, new ValueBuilder<int>(20)),
        };
        var builder = new SortedArrayBuilder<int, int>(items);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(2, blob.Value.Length);
        Assert.AreEqual(10, blob.Value[1]);
        Assert.AreEqual(20, blob.Value[2]);
    }

    [Test]
    public void should_handle_single_element()
    {
        var map = new Dictionary<int, int> { { 42, 999 } };
        var builder = new SortedArrayBuilder<int, int>(map);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(1, blob.Value.Length);
        Assert.AreEqual(999, blob.Value[42]);
    }

    [Test]
    public void should_expose_builders_via_property()
    {
        var items = new (int key, IBuilder<int> builder)[]
        {
            (1, new ValueBuilder<int>(10)),
            (2, new ValueBuilder<int>(20)),
        };
        var builder = new SortedArrayBuilder<int, int>(items);
        Assert.AreEqual(2, builder.Builders.Count);
        Assert.IsNotNull(builder[1]);
        Assert.IsNotNull(builder[2]);
    }
}

public class TestUnityBlobString
{
    [Test]
    public void should_create_empty_unity_string()
    {
        var builder = new UnityStringBuilder("");
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual("", blob.Value.ToString());
        Assert.AreEqual(0, blob.Value.Length);
    }

    [Test]
    public void should_create_ascii_unity_string()
    {
        const string text = "Hello Unity";
        var builder = new UnityStringBuilder(text);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(text, blob.Value.ToString());
        Assert.AreEqual(new UTF8Encoding().GetByteCount(text), blob.Value.Length);
    }

    [Test]
    public void should_create_unicode_unity_string()
    {
        const string text = "日本語テスト";
        var builder = new UnityStringBuilder(text);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual(text, blob.Value.ToString());
    }

    [Test]
    public void should_provide_span()
    {
        const string text = "Span";
        var builder = new UnityStringBuilder(text);
        var blob = builder.CreateManagedBlobAssetReference();
        var span = blob.Value.ToSpan();
        var expected = new UTF8Encoding().GetBytes(text);
        Assert.AreEqual(expected.Length, span.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.AreEqual(expected[i], span[i]);
    }

    [Test]
    public void should_handle_default_builder()
    {
        var builder = new UnityStringBuilder();
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.AreEqual("", blob.Value.ToString());
        Assert.AreEqual(0, blob.Value.Length);
    }
}
