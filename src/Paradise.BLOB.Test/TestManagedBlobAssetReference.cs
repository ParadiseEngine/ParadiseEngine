using System.Runtime.InteropServices;

namespace Paradise.BLOB.Test;

public class TestManagedBlobAssetReference
{
    struct SimpleValue
    {
        public int X;
        public float Y;
    }

    private static byte[] CreateBlob(int x, float y)
    {
        var builder = new ValueBuilder<SimpleValue>();
        builder.Value.X = x;
        builder.Value.Y = y;
        return builder.CreateBlob();
    }

    [Test]
    public unsafe void should_construct_and_access_value()
    {
        var blob = CreateBlob(42, 3.14f);
        using var reference = new ManagedBlobAssetReference<SimpleValue>(blob);

        Assert.AreEqual(42, reference.Value.X);
        Assert.AreEqual(3.14f, reference.Value.Y);
    }

    [Test]
    public unsafe void should_access_via_unsafe_ptr()
    {
        var blob = CreateBlob(99, 1.5f);
        using var reference = new ManagedBlobAssetReference<SimpleValue>(blob);

        SimpleValue* ptr = reference.UnsafePtr;
        Assert.AreEqual(99, ptr->X);
        Assert.AreEqual(1.5f, ptr->Y);
    }

    [Test]
    public void should_not_throw_on_double_dispose()
    {
        var blob = CreateBlob(1, 2.0f);
        var reference = new ManagedBlobAssetReference<SimpleValue>(blob);

        reference.Dispose();
        reference.Dispose();
    }
}
