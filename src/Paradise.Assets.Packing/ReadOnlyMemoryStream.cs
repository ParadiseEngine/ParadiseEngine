namespace Paradise.Assets.Packing;

/// <summary>Gives each reader its own cursor over borrowed immutable memory.</summary>
internal sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> memory) : Stream
{
    private long _position;
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get { Check(); return memory.Length; } }
    public override long Position
    {
        get { Check(); return _position; }
        set { Check(); ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }
    public override int Read(Span<byte> buffer)
    {
        Check();
        if (_position >= memory.Length) return 0;
        int count = Math.Min(buffer.Length, memory.Length - (int)_position);
        memory.Span.Slice((int)_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long start = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => Position,
            SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Position = checked(start + offset);
        return Position;
    }

    public override void Flush() => Check();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
    private void Check() => ObjectDisposedException.ThrowIf(_disposed, this);
}
