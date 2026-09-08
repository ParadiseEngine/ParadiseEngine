using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>World-owned event buffers delivered one tick after emission.</summary>
/// <remarks>Incoming events participate in <c>World.CopyFrom</c> snapshots and survive save/replay.</remarks>
public sealed class WorldEventStore
{
    private ISystemEvents?[] _byType = Array.Empty<ISystemEvents?>();

    // Owner-thread emissions merge after system writers and are never snapshotted.
    private readonly SystemEventWriter _managed = new();

    internal WorldEventStore()
    {
    }

    /// <summary>Events of type <typeparamref name="T"/> produced last frame; empty if none.</summary>
    /// <typeparam name="T">The unmanaged event type.</typeparam>
    /// <returns>A read-only span over the incoming events (read-many, non-destructive).</returns>
    public ReadOnlySpan<T> Incoming<T>() where T : unmanaged
    {
        int id = SystemEventType<T>.Id;
        if (id >= _byType.Length || _byType[id] is not SystemEvents<T> events)
            return ReadOnlySpan<T>.Empty;
        return events.Incoming;
    }

    /// <summary>Restores incoming events from a save for the next tick's readers.</summary>
    /// <remarks>Call outside a schedule run.</remarks>
    /// <typeparam name="T">The unmanaged event type.</typeparam>
    /// <param name="events">The incoming events to restore (copied in).</param>
    public void SetIncoming<T>(ReadOnlySpan<T> events) where T : unmanaged
    {
        int id = SystemEventType<T>.Id;
        EnsureCapacity(id + 1);
        var buffer = (SystemEvents<T>)(_byType[id] ??= new SystemEvents<T>());
        buffer.SetIncoming(events);
    }

    /// <summary>Stages an event for next tick, after all system-writer events.</summary>
    /// <remarks>Call on the world's owner thread, outside a schedule run.</remarks>
    /// <typeparam name="T">The unmanaged event type.</typeparam>
    /// <param name="e">The event value.</param>
    public void Emit<T>(in T e) where T : unmanaged => _managed.Append(in e);

    /// <summary>Publishes writers in schedule order, then managed events, replacing last tick's events.</summary>
    internal void Commit(ReadOnlySpan<SystemEventWriter> writers)
    {
        for (int i = 0; i < _byType.Length; i++)
            _byType[i]?.ResetStaging();

        foreach (var writer in writers)
            Dispatch(writer.Written);

        // Managed emits come after every system writer — a fixed, deterministic position in the merge.
        Dispatch(_managed.Written);
        _managed.Clear();

        for (int i = 0; i < _byType.Length; i++)
            _byType[i]?.PublishStaging();
    }

    /// <summary>Copies every type's incoming set from <paramref name="source"/> (snapshot copy).</summary>
    internal void CopyFrom(WorldEventStore source)
    {
        // Snapshot copies discard outgoing events.
        _managed.Clear();
        EnsureCapacity(source._byType.Length);
        for (int id = 0; id < _byType.Length; id++)
        {
            var src = id < source._byType.Length ? source._byType[id] : null;
            if (src is null)
            {
                _byType[id]?.Clear();
                continue;
            }
            (_byType[id] ??= SystemEventTypeRegistry.Create(id)).CopyIncomingFrom(src);
        }
    }

    /// <summary>Clears every type's events (used by <see cref="World{TMask,TConfig}.Clear"/>).</summary>
    internal void Clear()
    {
        _managed.Clear();
        for (int i = 0; i < _byType.Length; i++)
            _byType[i]?.Clear();
    }

    private void Dispatch(ReadOnlySpan<byte> stream)
    {
        int headerSize = Unsafe.SizeOf<SystemEventRecord>();
        int offset = 0;
        while (offset < stream.Length)
        {
            ref readonly var header = ref Unsafe.As<byte, SystemEventRecord>(ref Unsafe.AsRef(in stream[offset]));
            int typeId = header.TypeId;
            int size = header.Size;
            var payload = stream.Slice(offset + headerSize, size);
            EnsureBuffer(typeId).StageRaw(payload);
            offset += headerSize + size;
        }
    }

    private ISystemEvents EnsureBuffer(int id)
    {
        EnsureCapacity(id + 1);
        return _byType[id] ??= SystemEventTypeRegistry.Create(id);
    }

    private void EnsureCapacity(int count)
    {
        if (_byType.Length >= count)
            return;
        int newLength = Math.Max(count, Math.Max(4, _byType.Length * 2));
        Array.Resize(ref _byType, newLength);
    }
}
