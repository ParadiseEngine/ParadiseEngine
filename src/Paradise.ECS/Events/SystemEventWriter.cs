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

/// <summary>
/// Per-work-item handle a system uses to emit events during a wave. Events are appended to a private
/// byte stream (no cross-thread contention). After the wave, the schedule commits every writer into
/// the world's event store in SCHEDULE order, so the merged event order is deterministic regardless
/// of threading — the same mechanism <see cref="EntityCommandBuffer"/> uses for structural changes.
/// See <c>docs/system-events.md</c>.
/// </summary>
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

        if (size > 0)
            MemoryMarshal.Write(dest.Slice(headerSize), in e);

        _buffer.Advance(headerSize + size);
    }

    /// <summary>The recorded byte stream, walked by <see cref="WorldEventStore.Commit"/>.</summary>
    internal ReadOnlySpan<byte> Written => _buffer.WrittenSpan;

    /// <summary>Resets the writer for reuse on the next frame.</summary>
    internal void Clear() => _buffer.Clear();
}
