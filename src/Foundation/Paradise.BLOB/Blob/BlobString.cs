using System.Text;

namespace Paradise.BLOB;

public unsafe struct BlobString<TEncoding> where TEncoding : Encoding, new()
{
    private static readonly TEncoding s_encoding = new TEncoding();

    internal BlobArray<byte> Data;
    public int Length => Data.Length;
    public byte* UnsafePtr => Data.UnsafePtr;
    public new string ToString() => s_encoding.GetString(Data.UnsafePtr, Data.Length);
    public System.Span<byte> ToSpan() => Data.ToSpan();
}
