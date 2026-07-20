using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>
/// The set of typed event buffers owned by a <see cref="World{TMask,TConfig}"/>, indexed by event-type
/// id. Holds each type's INCOMING buffer (events produced last frame). It rides
/// <see cref="World{TMask,TConfig}.CopyFrom"/>, so events participate in the immutable snapshot — the
/// property that lets one-frame-deferred events survive a save and replay identically (see
/// <c>docs/system-events.md</c> §2, §6).
/// </summary>
public sealed class WorldEventStore
{
    private ISystemEvents?[] _byType = Array.Empty<ISystemEvents?>();

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

    /// <summary>
    /// Post-wave commit: replaces every type's incoming buffer with the events the writers recorded
    /// this frame, walked in schedule order (so the merge is deterministic in <paramref name="writers"/>
    /// order). Types with no new events this frame get an empty incoming — last frame's events expire.
    /// </summary>
    internal void Commit(ReadOnlySpan<SystemEventWriter> writers)
    {
        for (int i = 0; i < _byType.Length; i++)
            _byType[i]?.ResetStaging();

        foreach (var writer in writers)
            Dispatch(writer.Written);

        for (int i = 0; i < _byType.Length; i++)
            _byType[i]?.PublishStaging();
    }

    /// <summary>Copies every type's incoming set from <paramref name="source"/> (snapshot copy).</summary>
    internal void CopyFrom(WorldEventStore source)
    {
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
            var payload = size > 0 ? stream.Slice(offset + headerSize, size) : ReadOnlySpan<byte>.Empty;
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
