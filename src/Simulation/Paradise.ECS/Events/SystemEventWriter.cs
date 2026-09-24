using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.ECS;

/// <summary>Header for one recorded event in a <see cref="SystemEventWriter"/> byte stream.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SystemEventRecord
{
    /// <summary>The event-type id (see <see cref="SystemEventType{T}"/>).</summary>
    public int TypeId;

    /// <summary>The marshalled byte size of the event payload following this header.</summary>
    public int Size;
}

/// <summary>Private event stream for one work item, committed in schedule order.</summary>
/// <remarks>Each writer stays on its worker thread; merge order is independent of worker completion.</remarks>
public sealed class SystemEventWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    /// <summary>Records one event of type <typeparamref name="T"/> for delivery to next frame's systems.</summary>
    /// <typeparam name="T">The unmanaged event type.</typeparam>
    /// <param name="e">The event value.</param>
    public void Append<T>(in T e) where T : unmanaged
    {
        int headerSize = Unsafe.SizeOf<SystemEventRecord>();
        int size = SystemEventType<T>.Size;
        var dest = _buffer.GetSpan(headerSize + size);

        ref var header = ref Unsafe.As<byte, SystemEventRecord>(ref dest[0]);
        header.TypeId = SystemEventType<T>.Id;
        header.Size = size;

        MemoryMarshal.Write(dest.Slice(headerSize), in e);

        _buffer.Advance(headerSize + size);
    }

    /// <summary>The recorded byte stream, walked by <see cref="WorldEventStore.Commit"/>.</summary>
    internal ReadOnlySpan<byte> Written => _buffer.WrittenSpan;

    /// <summary>Resets the writer for reuse on the next frame.</summary>
    internal void Clear() => _buffer.Clear();
}
