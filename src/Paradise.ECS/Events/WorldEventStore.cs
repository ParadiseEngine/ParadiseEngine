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

    // Managed (non-system) emit staging. Managed code — command handlers, a host post-pass — appends
    // here via Emit; Commit dispatches it alongside the per-work-item system writers (after them, so
    // the merge stays deterministic) and clears it. It is transient like a system writer: drained
    // every commit, never part of the snapshot. See Emit / Commit.
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

    /// <summary>
    /// Directly replaces the incoming buffer for event type <typeparamref name="T"/> — used to
    /// RE-SEED events on load (a save taken while an event was in-flight, produced tick N but not yet
    /// consumed until N+1, must restore it here). Call OUTSIDE a schedule run; the next tick's readers
    /// observe it exactly as if it had been committed by the previous frame. See docs/system-events.md.
    /// </summary>
    /// <typeparam name="T">The unmanaged event type.</typeparam>
    /// <param name="events">The incoming events to restore (copied in).</param>
    public void SetIncoming<T>(ReadOnlySpan<T> events) where T : unmanaged
    {
        int id = SystemEventType<T>.Id;
        EnsureCapacity(id + 1);
        var buffer = (SystemEvents<T>)(_byType[id] ??= new SystemEvents<T>());
        buffer.SetIncoming(events);
    }

    /// <summary>
    /// Emits one event of type <typeparamref name="T"/> from MANAGED code (outside a schedule run) —
    /// the non-system sibling of <see cref="SystemEventWriter.Append{T}"/>. Staged now, dispatched by
    /// the next <see cref="Commit"/> after all system writers, so it is delivered to next frame's
    /// readers with identical one-frame-deferred, deterministic semantics.
    /// <para>NOT thread-safe: call on the world's owner thread only (the same contract as
    /// <c>world.GetComponent&lt;T&gt;().Value = …</c>), and outside a schedule run so it never races the
    /// per-work-item writers. See <c>docs/system-events.md</c> §4.2.</para>
    /// </summary>
    /// <typeparam name="T">The unmanaged event type.</typeparam>
    /// <param name="e">The event value.</param>
    public void Emit<T>(in T e) where T : unmanaged => _managed.Append(in e);

    /// <summary>
    /// Post-wave commit: replaces every type's incoming buffer with the events the writers recorded
    /// this frame, walked in schedule order (so the merge is deterministic in <paramref name="writers"/>
    /// order). Managed events emitted via <see cref="Emit"/> this frame merge last (also deterministic).
    /// Types with no new events this frame get an empty incoming — last frame's events expire.
    /// </summary>
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
        // Managed staging is transient (never snapshotted); a write world adopting a snapshot starts
        // the tick with an empty outgoing buffer, before any ProcessCommands Emit.
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
