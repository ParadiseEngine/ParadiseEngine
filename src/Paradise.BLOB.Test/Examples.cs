using System.Text;

namespace Paradise.BLOB.Test;

public class Examples
{
    [Test]
    public void int_blob()
    {
        var builder = new ValueBuilder<int>(1);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.That(blob.Value, Is.EqualTo(1));
    }

    [Test]
    public void int_array_blob()
    {
        var builder = new ArrayBuilder<int>(new [] { 1, 2, 3 });
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.That(blob.Value.ToArray(), Is.EquivalentTo(new [] { 1, 2, 3}));
    }

    [Test]
    public void string_blob()
    {
        var builder = new StringBuilder<UTF8Encoding>("123");
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.That(blob.Value.ToString(), Is.EquivalentTo("123"));
    }

    [Test]
    public void int_ptr_with_new_value_blob()
    {
        var builder = new PtrBuilderWithNewValue<int>(1);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.That(blob.Value.Value, Is.EqualTo(1));
    }

    struct IntPtr
    {
        public int Int;
        public BlobPtr<int> Ptr;
    }

    [Test]
    public void int_ptr_to_int_blob()
    {
        var builder = new StructBuilder<IntPtr>();
        var valueBuilder = builder.SetValue(ref builder.Value.Int, 1);
        builder.SetPointer(ref builder.Value.Ptr, valueBuilder);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.That(blob.Value.Int, Is.EqualTo(1));
        Assert.That(blob.Value.Ptr.Value, Is.EqualTo(1));
    }

    struct PtrPtr
    {
        public BlobPtr<int> Ptr1;
        public BlobPtr<int> Ptr2;
    }

    [Test]
    public void int_ptr_to_another_ptr_value_blob()
    {
        var builder = new StructBuilder<PtrPtr>();
        var valueBuilder = builder.SetPointer(ref builder.Value.Ptr1, 1).ValueBuilder;
        builder.SetPointer(ref builder.Value.Ptr2, valueBuilder);
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.That(blob.Value.Ptr1.Value, Is.EqualTo(1));
        Assert.That(blob.Value.Ptr2.Value, Is.EqualTo(1));
    }

    struct Blob
    {
        public int Int;
        public BlobString<UTF8Encoding> String;
        public BlobPtr<BlobString<UTF8Encoding>> PtrString;
        public BlobArray<int> IntArray;
    }

    [Test]
    public void struct_blob()
    {
        var builder = new StructBuilder<Blob>();
        builder.SetValue(ref builder.Value.Int, 1);
        var stringBuilder = builder.SetString(ref builder.Value.String, "123");
        builder.SetPointer(ref builder.Value.PtrString, stringBuilder);
        builder.SetArray(ref builder.Value.IntArray, new[] { 1, 2, 3 });
        var blob = builder.CreateManagedBlobAssetReference();
        Assert.That(blob.Value.Int, Is.EqualTo(1));
        Assert.That(blob.Value.String.ToString(), Is.EqualTo("123"));
        Assert.That(blob.Value.PtrString.Value.ToString(), Is.EqualTo("123"));
        Assert.That(blob.Value.IntArray.ToArray(), Is.EqualTo(new [] {1, 2, 3}));
    }
}
