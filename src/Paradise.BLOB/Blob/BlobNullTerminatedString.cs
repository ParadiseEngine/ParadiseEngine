using System.Text;

namespace Paradise.BLOB;

public unsafe struct BlobNullTerminatedString<TEncoding> where TEncoding : Encoding, new()
{
    private static readonly TEncoding s_encoding = new TEncoding();

    internal BlobArray<byte> Data;
    public int Length => Data.Length - 1/* length without termination */;
    public byte* UnsafePtr => Data.UnsafePtr;
    public new string ToString() => s_encoding.GetString(Data.UnsafePtr, Length);
#if UNITY_2021_2_OR_NEWER || NETSTANDARD2_1_OR_GREATER
    public System.Span<byte> ToSpan() => new System.Span<byte>(UnsafePtr, Length);
#endif
}
