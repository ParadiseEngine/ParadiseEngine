
namespace Paradise.BLOB;

public interface IBlobStream
{
    int Alignment { get; set; }
    int PatchPosition { get; set; }
    int Position { get; set; }
    int Length { get; set; }
    byte[] ToArray();
    byte[] Buffer { get; }
    unsafe void Write(byte* valuePtr, int size, int alignment);
}

public static partial class BlobStreamExtension
{
    public static unsafe void Write(this IBlobStream stream, byte* valuePtr, int size) => stream.Write(valuePtr, size, stream.Alignment);
    public static int GetAlignment(this IBlobStream stream, int alignment) => alignment > 0 ? alignment : stream.Alignment;
}
