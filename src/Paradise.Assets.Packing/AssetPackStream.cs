using System.Buffers;
using System.Security.Cryptography;

using ZstdSharp;

namespace Paradise.Assets.Packing;

/// <summary>A logical asset with one independently validated chunk cached at a time.</summary>
internal sealed class AssetPackStream(Stream source, PackIndex index, PackEntry entry) : Stream
{
    private Stream? _source = source;
    private Decompressor? _decoder;
    private byte[]? _raw;
    private byte[]? _stored;
    private int _cachedChunk = -1;
    private long _position;

    public override bool CanRead => _source is not null;
    public override bool CanSeek => _source is not null;
    public override bool CanWrite => false;
    public override long Length { get { ThrowIfDisposed(); return entry.Length; } }
    public override long Position
    {
        get { ThrowIfDisposed(); return _position; }
        set
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty || _position >= entry.Length) return 0;
        int chunkId = entry.FirstChunk + (int)(_position / index.ChunkSize);
        if (_cachedChunk != chunkId)
        {
            var chunk = Prepare(chunkId);
            try
            {
                _source!.ReadExactly(_stored.AsSpan(0, chunk.StoredLength));
            }
            catch (EndOfStreamException error)
            {
                throw new InvalidDataException($"Truncated chunk in '{entry.Path}'.", error);
            }

            Decode(chunkId, chunk);
        }

        return Copy(buffer, chunkId);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty || _position >= entry.Length) return 0;
        int chunkId = entry.FirstChunk + (int)(_position / index.ChunkSize);
        if (_cachedChunk != chunkId)
        {
            var chunk = Prepare(chunkId);
            try
            {
                await _source!.ReadExactlyAsync(_stored.AsMemory(0, chunk.StoredLength), cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException error)
            {
                throw new InvalidDataException($"Truncated chunk in '{entry.Path}'.", error);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Decode(chunkId, chunk);
        }

        return Copy(buffer.Span, chunkId);
    }

    private PackChunk Prepare(int chunkId)
    {
        _cachedChunk = -1;
        _raw ??= ArrayPool<byte>.Shared.Rent(index.ChunkSize);
        _stored ??= ArrayPool<byte>.Shared.Rent(index.ChunkSize);
        var chunk = index.Chunks[chunkId];
        _source!.Position = chunk.Offset;
        return chunk;
    }

    private void Decode(int chunkId, PackChunk chunk)
    {
        var stored = _stored.AsSpan(0, chunk.StoredLength);
        var raw = _raw.AsSpan(0, chunk.RawLength);
        if (chunk.IsCompressed)
        {
            try
            {
                _decoder ??= new Decompressor();
                if (Decompressor.GetDecompressedSize(stored) != (ulong)chunk.RawLength || _decoder.Unwrap(stored, raw) != chunk.RawLength)
                    throw new InvalidDataException($"Decoded chunk length mismatch in '{entry.Path}'.");
            }
            catch (ZstdException error)
            {
                throw new InvalidDataException($"Invalid Zstandard chunk in '{entry.Path}'.", error);
            }
        }
        else
        {
            stored.CopyTo(raw);
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(raw, hash);
        if (!hash.SequenceEqual(chunk.Hash)) throw new InvalidDataException($"Chunk checksum mismatch in '{entry.Path}'.");
        _cachedChunk = chunkId;
    }

    private int Copy(Span<byte> destination, int chunkId)
    {
        int offset = (int)(_position % index.ChunkSize);
        int count = Math.Min(destination.Length, index.Chunks[chunkId].RawLength - offset);
        _raw.AsSpan(offset, count).CopyTo(destination);
        _position += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        long start = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => _position,
            SeekOrigin.End => entry.Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        long position;
        try { position = checked(start + offset); }
        catch (OverflowException error) { throw new IOException("Seek exceeds the stream address space.", error); }
        if (position < 0) throw new IOException("Cannot seek before the start of an asset.");
        return _position = position;
    }

    public override void Flush() => ThrowIfDisposed();
    public override void SetLength(long value) => throw new NotSupportedException("Asset packs are read-only.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Asset packs are read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing && _source is { } input)
        {
            _source = null;
            try { input.Dispose(); }
            finally
            {
                _decoder?.Dispose();
                if (_raw is not null) ArrayPool<byte>.Shared.Return(_raw);
                if (_stored is not null) ArrayPool<byte>.Shared.Return(_stored);
                _raw = null;
                _stored = null;
            }
        }

        base.Dispose(disposing);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_source is null, this);
}
