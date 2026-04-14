using System;
using System.Text;

namespace Paradise.BLOB;

public class StringBuilder<TEncoding> : ArrayBuilder<byte, BlobString<TEncoding>>
    where TEncoding : Encoding, new()
{
    public StringBuilder() : base(Array.Empty<byte>()) {}
    public StringBuilder(string str) : base(new TEncoding().GetBytes(str)) {}
}
