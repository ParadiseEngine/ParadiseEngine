using System.Security.Cryptography;

using Zio;

using ZstdSharp;

namespace Paradise.Assets.Packing;

/// <summary>One logical pack path and its absolute location in the source filesystem.</summary>
public sealed record AssetPackFile(string Path, UPath Source);

/// <summary>Settings for independently compressed asset chunks.</summary>
public sealed class AssetPackOptions
{
    public int ChunkSize { get; init; } = 256 * 1024;

    public int CompressionLevel { get; init; } = 3;
}

/// <summary>Writes deterministic asset packs from an explicit set of Zio files.</summary>
public static class AssetPackWriter
{
    public const string FileName = "assets.pak";

    /// <summary>Replaces the destination contents, leaving the stream and source filesystem open.</summary>
    /// <remarks>The destination must be seekable and must not be one of the source files; callers publish it only after this method succeeds.</remarks>
    public static void Write(
        IFileSystem source,
        Stream destination,
        IEnumerable<AssetPackFile> files,
        AssetPackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(files);
        options ??= new AssetPackOptions();
        if (!destination.CanWrite || !destination.CanSeek) throw new ArgumentException("A writable, seekable destination is required.", nameof(destination));
        PackFormat.ValidateChunkSize(options.ChunkSize);
        if (options.CompressionLevel is < 1 or > 22) throw new ArgumentOutOfRangeException(nameof(options), "Compression level must be between 1 and 22.");

        var ordered = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        PackFormat.ValidatePaths(ordered.Select(file => file.Path));
        foreach (var file in ordered)
        {
            if (!file.Source.IsAbsolute) throw new ArgumentException($"Source '{file.Source}' must be absolute.", nameof(files));
        }

        cancellationToken.ThrowIfCancellationRequested();
        destination.Position = 0;
        destination.SetLength(0);
        using var writer = new BinaryWriter(destination, PackFormat.Utf8, leaveOpen: true);
        writer.Write(PackFormat.HeaderMagic);
        writer.Write(PackFormat.Version);
        writer.Write(options.ChunkSize);
        writer.Write(0L);

        var entries = new List<PackEntry>(ordered.Length);
        var chunks = new List<PackChunk>();
        var raw = new byte[options.ChunkSize];
        var compressed = new byte[Compressor.GetCompressBound(raw.Length)];
        using var compressor = new Compressor(options.CompressionLevel);
        foreach (var file in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = source.OpenFile(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = 0;
            int firstChunk = chunks.Count;
            bool store = file.Path.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase);
            int count;
            while ((count = input.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int encodedLength = count;
                if (!store)
                {
                    try
                    {
                        encodedLength = compressor.Wrap(raw.AsSpan(0, count), compressed);
                    }
                    catch (ZstdException error)
                    {
                        throw new InvalidDataException($"Cannot compress '{file.Path}'.", error);
                    }
                }

                bool useZstd = !store && encodedLength < count;
                int storedLength = useZstd ? encodedLength : count;
                chunks.Add(new PackChunk(destination.Position, storedLength, count, useZstd,
                    SHA256.HashData(raw.AsSpan(0, count))));
                destination.Write((useZstd ? compressed : raw).AsSpan(0, storedLength));
                length = checked(length + count);
            }

            entries.Add(new PackEntry(file.Path, length, firstChunk, chunks.Count - firstChunk));
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] toc = PackFormat.WriteToc(entries, chunks);
        long tocOffset = destination.Position;
        destination.Write(toc);
        writer.Write(PackFormat.FooterMagic);
        writer.Write(tocOffset);
        writer.Write((long)toc.Length);
        writer.Write(SHA256.HashData(toc));
        writer.Write(0L);
        writer.Flush();
    }
}
