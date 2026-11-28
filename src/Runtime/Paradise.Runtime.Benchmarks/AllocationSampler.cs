using System.Diagnostics.Tracing;

namespace Paradise.Runtime.Benchmarks;

/// <summary>Which types are allocated, from the runtime's <c>GCAllocationTick</c> events.</summary>
/// <remarks>The runtime raises one event about every 100 KB allocated per thread, naming the type of the
/// allocation that crossed the threshold, so a type's tick count times 100 KB estimates its bytes. Events
/// arrive on the runtime's dispatch thread, whose own allocations land outside the phases' thread-local
/// counts but inside the process total.</remarks>
internal sealed class AllocationSampler : EventListener
{
    private const string RuntimeSource = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private const string AllocationTick = "GCAllocationTick_V4";

    private readonly object _gate = new();
    private readonly Dictionary<string, (long Ticks, long Bytes)> _types = new(StringComparer.Ordinal);
    private EventSource? _runtime;
    private volatile bool _recording;

    public bool Recording
    {
        get => _recording;
        set => _recording = value;
    }

    /// <summary>Starts listening; the base constructor may report the runtime source before fields exist,
    /// so events are enabled here rather than in <see cref="EventListener.OnEventSourceCreated(EventSource)"/>.</summary>
    public void Start()
    {
        foreach (var source in EventSource.GetSources())
            if (source.Name == RuntimeSource) _runtime = source;
        if (_runtime is null) throw new InvalidOperationException("The runtime event source is unavailable.");
        EnableEvents(_runtime, EventLevel.Verbose, GcKeyword);
    }

    /// <summary>Types by sampled ticks, heaviest first.</summary>
    public IReadOnlyList<(string Type, long Ticks, long Bytes)> Snapshot()
    {
        lock (_gate)
            return [.. _types.Select(entry => (entry.Key, entry.Value.Ticks, entry.Value.Bytes)).OrderByDescending(entry => entry.Ticks)];
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!_recording || eventData.EventName != AllocationTick || eventData.Payload is not { } payload) return;
        var type = Field(eventData, payload, "TypeName") as string ?? "?";
        var amount = Field(eventData, payload, "AllocationAmount64") is ulong bytes ? (long)bytes : 0;
        lock (_gate)
        {
            _types.TryGetValue(type, out var seen);
            _types[type] = (seen.Ticks + 1, seen.Bytes + amount);
        }
    }

    private static object? Field(EventWrittenEventArgs eventData, IReadOnlyList<object?> payload, string name)
    {
        var index = eventData.PayloadNames?.IndexOf(name) ?? -1;
        return index >= 0 && index < payload.Count ? payload[index] : null;
    }
}
