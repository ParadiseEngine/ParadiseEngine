using System.Runtime.InteropServices;

namespace Paradise.ECS;

/// <summary>Untyped access to buffers held by <see cref="WorldEventStore"/>.</summary>
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

/// <summary>Stores last tick's incoming events and stages replacements for the next tick.</summary>
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
        if (_stagingCount == _staging.Length)
            Array.Resize(ref _staging, Math.Max(4, _staging.Length * 2));
        _staging[_stagingCount++] = e;
    }

    public void PublishStaging()
    {
        // Reuse the old incoming array as next tick's staging buffer.
        (_incoming, _staging) = (_staging, _incoming);
        _incomingCount = _stagingCount;
        _stagingCount = 0;
    }

    public void SetIncoming(ReadOnlySpan<T> events)
    {
        if (_incoming.Length < events.Length)
            _incoming = new T[events.Length];
        events.CopyTo(_incoming);
        _incomingCount = events.Length;
    }

    public void CopyIncomingFrom(ISystemEvents source)
        => SetIncoming(((SystemEvents<T>)source).Incoming);

    public void Clear()
    {
        _incomingCount = 0;
        _stagingCount = 0;
    }
}
