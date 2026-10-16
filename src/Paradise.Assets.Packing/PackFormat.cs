using System.Security.Cryptography;
using System.Text;

namespace Paradise.Assets.Packing;

internal sealed record PackEntry(string Path, long Length, int FirstChunk, int ChunkCount);

internal sealed record PackChunk(long Offset, int StoredLength, int RawLength, bool IsCompressed, byte[] Hash);

internal sealed record PackIndex(int ChunkSize, PackEntry[] Entries, PackChunk[] Chunks);

internal static class PackFormat
{
    internal const int Version = 1;
    internal const int HeaderSize = 24;
    internal const int FooterSize = 64;
    internal const int ChunkRecordSize = 52;
    internal const int MaxTocSize = 64 * 1024 * 1024;
    internal const int MaxPathSize = 4096;
    internal static ReadOnlySpan<byte> HeaderMagic => "PDPACK\0\0"u8;
    internal static ReadOnlySpan<byte> FooterMagic => "PDPAKEND"u8;
    internal static UTF8Encoding Utf8 { get; } = new(false, true);

    internal static void ValidateChunkSize(int size)
    {
        if (size is < 1024 or > 1024 * 1024 || (size & (size - 1)) != 0)
            throw new InvalidDataException("Pack chunks must be a power of two between 1 KiB and 1 MiB.");
    }

    internal static void ValidatePaths(IEnumerable<string> paths)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)
                || path.Split('/').Any(segment => segment is "" or "." or "..") || Utf8.GetByteCount(path) > MaxPathSize)
                throw new InvalidDataException($"Invalid pack path '{path}'.");
            if (!names.Add(path)) throw new InvalidDataException($"Duplicate pack path '{path}'.");
        }

        foreach (string path in names)
        {
            for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                if (names.Contains(path[..slash])) throw new InvalidDataException($"Pack file '{path[..slash]}' is also a directory.");
            }
        }
    }

    internal static byte[] WriteToc(List<PackEntry> entries, List<PackChunk> chunks)
    {
        long size = 8L + (long)chunks.Count * ChunkRecordSize;
        foreach (var entry in entries) size += 20 + Utf8.GetByteCount(entry.Path);
        if (size > MaxTocSize) throw new InvalidDataException("Pack TOC exceeds 64 MiB.");
        using var memory = new MemoryStream((int)size);
        using var writer = new BinaryWriter(memory, Utf8, leaveOpen: true);
        writer.Write(entries.Count);
        writer.Write(chunks.Count);
        foreach (var entry in entries)
        {
            byte[] path = Utf8.GetBytes(entry.Path);
            writer.Write(path.Length);
            writer.Write(path);
            writer.Write(entry.Length);
            writer.Write(entry.FirstChunk);
            writer.Write(entry.ChunkCount);
        }

        foreach (var chunk in chunks)
        {
            writer.Write(chunk.Offset);
            writer.Write(chunk.StoredLength);
            writer.Write(chunk.RawLength);
            writer.Write(chunk.IsCompressed ? 1 : 0);
            writer.Write(chunk.Hash);
        }

        return memory.ToArray();
    }

    internal static PackIndex ReadIndex(Stream input)
    {
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("A readable, seekable pack is required.", nameof(input));
        try
        {
            return ReadIndexCore(input);
        }
        catch (Exception error) when (error is EndOfStreamException or OverflowException or DecoderFallbackException)
        {
            throw new InvalidDataException("Invalid or truncated asset pack metadata.", error);
        }
    }

    private static PackIndex ReadIndexCore(Stream input)
    {
        if (input.Length < HeaderSize + FooterSize + 8) throw new InvalidDataException("Truncated asset pack.");
        using var reader = new BinaryReader(input, Utf8, leaveOpen: true);
        input.Position = 0;
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual(HeaderMagic) || reader.ReadInt32() != Version)
            throw new InvalidDataException("Unsupported asset pack magic or version.");
        int chunkSize = reader.ReadInt32();
        ValidateChunkSize(chunkSize);
        if (reader.ReadInt64() != 0) throw new InvalidDataException("Unsupported asset pack flags.");

        input.Position = input.Length - FooterSize;
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual(FooterMagic)) throw new InvalidDataException("Missing asset pack footer.");
        long tocOffset = reader.ReadInt64();
        long tocLength = reader.ReadInt64();
        byte[] expectedHash = reader.ReadBytes(SHA256.HashSizeInBytes);
        if (reader.ReadInt64() != 0 || tocOffset < HeaderSize || tocLength is < 8 or > MaxTocSize
            || tocOffset > input.Length - FooterSize || tocLength != input.Length - FooterSize - tocOffset)
            throw new InvalidDataException("Invalid asset pack TOC bounds.");
        input.Position = tocOffset;
        byte[] bytes = new byte[(int)tocLength];
        input.ReadExactly(bytes);
        if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(expectedHash)) throw new InvalidDataException("Asset pack TOC checksum mismatch.");

        using var memory = new MemoryStream(bytes, writable: false);
        using var toc = new BinaryReader(memory, Utf8);
        int entryCount = toc.ReadInt32();
        int chunkCount = toc.ReadInt32();
        if (entryCount < 0 || chunkCount < 0 || (long)entryCount * 21 + (long)chunkCount * ChunkRecordSize > tocLength - 8)
            throw new InvalidDataException("Invalid asset pack TOC counts.");
        var entries = new PackEntry[entryCount];
        int nextChunk = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            int pathLength = toc.ReadInt32();
            if (pathLength is < 1 or > MaxPathSize || pathLength > memory.Length - memory.Position)
                throw new InvalidDataException("Invalid asset pack path length.");
            string path = Utf8.GetString(toc.ReadBytes(pathLength));
            long length = toc.ReadInt64();
            int first = toc.ReadInt32();
            int count = toc.ReadInt32();
            long expectedCount = length <= 0 ? 0 : (length - 1) / chunkSize + 1;
            if (length < 0 || first != nextChunk || count < 0 || count != expectedCount || count > chunkCount - nextChunk
                || (i > 0 && string.CompareOrdinal(entries[i - 1].Path, path) >= 0))
                throw new InvalidDataException("Invalid asset entry or chunk range.");
            entries[i] = new PackEntry(path, length, first, count);
            nextChunk += count;
        }

        ValidatePaths(entries.Select(entry => entry.Path));
        if (nextChunk != chunkCount || memory.Length - memory.Position != (long)chunkCount * ChunkRecordSize)
            throw new InvalidDataException("Asset pack has unreferenced chunks or trailing TOC data.");
        var chunks = new PackChunk[chunkCount];
        long nextOffset = HeaderSize;
        for (int i = 0; i < chunks.Length; i++)
        {
            long offset = toc.ReadInt64();
            int stored = toc.ReadInt32();
            int raw = toc.ReadInt32();
            int codec = toc.ReadInt32();
            byte[] hash = toc.ReadBytes(SHA256.HashSizeInBytes);
            if (offset != nextOffset || stored <= 0 || raw <= 0 || raw > chunkSize || codec is < 0 or > 1
                || (codec == 0 ? stored != raw : stored >= raw) || stored > tocOffset - offset)
                throw new InvalidDataException("Invalid asset pack chunk bounds or codec.");
            chunks[i] = new PackChunk(offset, stored, raw, codec == 1, hash);
            nextOffset += stored;
        }

        if (nextOffset != tocOffset) throw new InvalidDataException("Unindexed asset pack payload.");
        foreach (var entry in entries)
        {
            for (int i = 0; i < entry.ChunkCount; i++)
            {
                int expected = (int)Math.Min(chunkSize, entry.Length - (long)i * chunkSize);
                if (chunks[entry.FirstChunk + i].RawLength != expected) throw new InvalidDataException("Asset chunk decoded lengths do not match its logical length.");
            }
        }

        return new PackIndex(chunkSize, entries, chunks);
    }
}
