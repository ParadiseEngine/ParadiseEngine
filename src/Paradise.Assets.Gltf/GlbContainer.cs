using System.Buffers.Binary;

namespace Paradise.Assets.Gltf;

/// <summary>Parsed GLB (binary glTF) container: the JSON chunk plus the optional BIN chunk.
/// Bounds-checked chunk walk over the caller's memory — no copies; the returned memories alias
/// the input.</summary>
public readonly struct GlbContainer
{
    private const uint Magic = 0x46546C67;     // "glTF"
    private const uint JsonChunkType = 0x4E4F534A; // "JSON"
    private const uint BinChunkType = 0x004E4942;  // "BIN\0"

    public ReadOnlyMemory<byte> Json { get; }
    public ReadOnlyMemory<byte> Bin { get; }

    private GlbContainer(ReadOnlyMemory<byte> json, ReadOnlyMemory<byte> bin)
    {
        Json = json;
        Bin = bin;
    }

    /// <summary>Parse the 12-byte header + chunk sequence. Throws <see cref="InvalidDataException"/>
    /// on anything malformed: bad magic, unsupported version, truncated chunks, missing JSON.</summary>
    public static GlbContainer Parse(ReadOnlyMemory<byte> glb)
    {
        var span = glb.Span;
        if (span.Length < 12)
            throw new InvalidDataException($"GLB too small ({span.Length} bytes) — the fixed header alone is 12 bytes.");

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (magic != Magic)
            throw new InvalidDataException($"Not a GLB: magic 0x{magic:X8} != 0x{Magic:X8} (\"glTF\").");

        var version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        if (version != 2)
            throw new InvalidDataException($"GLB version {version} is not supported (only 2).");

        var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        if (totalLength > (uint)span.Length)
            throw new InvalidDataException($"GLB header declares {totalLength} bytes but only {span.Length} were provided.");

        ReadOnlyMemory<byte> json = default;
        ReadOnlyMemory<byte> bin = default;
        var offset = 12;
        while (offset + 8 <= (int)totalLength)
        {
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 4)..]);
            offset += 8;
            if (chunkLength > (uint)((int)totalLength - offset))
                throw new InvalidDataException(
                    $"Chunk 0x{chunkType:X8} declares {chunkLength} bytes but only {(int)totalLength - offset} remain.");

            var chunk = glb.Slice(offset, (int)chunkLength);
            if (chunkType == JsonChunkType && json.IsEmpty) json = chunk;
            else if (chunkType == BinChunkType && bin.IsEmpty) bin = chunk;
            // Unknown chunk types are skipped per spec.

            // Chunks are 4-byte aligned; length excludes padding.
            offset += (int)chunkLength;
            offset += (4 - (offset & 3)) & 3;
        }

        if (json.IsEmpty)
            throw new InvalidDataException("GLB has no JSON chunk.");

        return new GlbContainer(json, bin);
    }
}
