using System.Runtime.InteropServices;

namespace Paradise.ECS;

/// <summary>
/// Type-erased handle to a typed event buffer, so <see cref="WorldEventStore"/> can store, copy,
/// and commit buffers of many event types uniformly.
/// </summary>
internal interface ISystemEvents
{
    /// <summary>Begins a new commit: discards any previously staged (outgoing) events.</summary>
    void ResetStaging();

    /// <summary>Stages one event from its raw marshalled bytes (dispatched from a writer stream).</summary>
    void StageRaw(ReadOnlySpan<byte> data);

    /// <summary>Publishes the staged events as the new incoming set (wholesale replace).</summary>
    void PublishStaging();

    /// <summary>Copies the incoming set from another buffer of the same type (snapshot copy).</summary>
    void CopyIncomingFrom(ISystemEvents source);

    /// <summary>Clears incoming and staged events.</summary>
    void Clear();
}

/// <summary>
/// World-owned buffer for one unmanaged event type. Holds the INCOMING events (produced last frame,
/// read-many by systems this frame). Outgoing events are staged during the post-wave commit and
/// published to incoming atomically, so last frame's events auto-expire. See
/// <c>docs/system-events.md</c>.
/// </summary>
/// <typeparam name="T">The unmanaged event type.</typeparam>
internal sealed class SystemEvents<T> : ISystemEvents where T : unmanaged
{
    private T[] _incoming = Array.Empty<T>();
    private int _incomingCount;
    private T[] _staging = Array.Empty<T>();
    private int _stagingCount;

    /// <summary>Events produced last frame; read-many by systems this frame.</summary>
    public ReadOnlySpan<T> Incoming => _incoming.AsSpan(0, _incomingCount);

    public void ResetStaging() => _stagingCount = 0;

    public void StageRaw(ReadOnlySpan<byte> data)
    {
        var e = MemoryMarshal.Read<T>(data);
        StageOne(in e);
    }

    public void StageOne(in T e)
    {
        if (_stagingCount == _staging.Length)
            Array.Resize(ref _staging, Math.Max(4, _staging.Length * 2));
        _staging[_stagingCount++] = e;
    }

    public void PublishStaging()
    {
        // Swap staging <-> incoming: incoming becomes exactly this frame's produced set, and the old
        // incoming array is retained as next frame's staging scratch (no steady-state allocation).
        (_incoming, _staging) = (_staging, _incoming);
        _incomingCount = _stagingCount;
        _stagingCount = 0;
    }

    public void CopyIncomingFrom(ISystemEvents source)
    {
        var src = (SystemEvents<T>)source;
        if (_incoming.Length < src._incomingCount)
            _incoming = new T[src._incomingCount];
        Array.Copy(src._incoming, _incoming, src._incomingCount);
        _incomingCount = src._incomingCount;
    }

    public void Clear()
    {
        _incomingCount = 0;
        _stagingCount = 0;
    }
}
