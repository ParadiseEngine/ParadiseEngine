using System.Linq;
using System.Text;

namespace Paradise.BLOB;

public class NullTerminatedStringBuilder<TEncoding> : ArrayBuilder<byte, BlobNullTerminatedString<TEncoding>>
    where TEncoding : Encoding, new()
{
    public NullTerminatedStringBuilder() : base(new byte[] { 0 }) {}
    public NullTerminatedStringBuilder(string str) : base(new TEncoding().GetBytes(str).Append((byte)0).ToArray()) {}
}
