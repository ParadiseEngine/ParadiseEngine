using System.Text;

namespace Paradise.BLOB;

public unsafe struct BlobString<TEncoding> where TEncoding : Encoding, new()
{
    private static readonly TEncoding s_encoding = new TEncoding();

    internal BlobArray<byte> Data;
    public int Length => Data.Length;
    public byte* UnsafePtr => Data.UnsafePtr;
    public new string ToString() => s_encoding.GetString(Data.UnsafePtr, Data.Length);
#if UNITY_2021_2_OR_NEWER || NETSTANDARD2_1_OR_GREATER
    public System.Span<byte> ToSpan() => Data.ToSpan();
#endif
}
