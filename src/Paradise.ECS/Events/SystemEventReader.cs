namespace Paradise.ECS;

/// <summary>
/// Read handle a system uses to observe events produced LAST frame (read-many, non-destructive).
/// It binds to the read world's event store in snapshot mode (the previous-tick snapshot the write
/// world was <c>CopyFrom</c>'d from), or the write world under classic <c>Run()</c>. Events are
/// therefore delivered with a one-frame latency — see <c>docs/system-events.md</c>.
/// </summary>
public readonly struct SystemEventReader : IEquatable<SystemEventReader>
{
    private readonly WorldEventStore? _store;

    /// <summary>Creates a reader over the given world's event store.</summary>
    /// <param name="store">The store to read incoming events from.</param>
    public SystemEventReader(WorldEventStore? store) => _store = store;

    /// <summary>Events of type <typeparamref name="T"/> produced last frame; empty if none.</summary>
    /// <typeparam name="T">The unmanaged event type.</typeparam>
    /// <returns>A read-only span over the incoming events (read-many, non-destructive).</returns>
    public ReadOnlySpan<T> Read<T>() where T : unmanaged =>
        _store is null ? ReadOnlySpan<T>.Empty : _store.Incoming<T>();

    /// <inheritdoc/>
    public bool Equals(SystemEventReader other) => ReferenceEquals(_store, other._store);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SystemEventReader other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _store?.GetHashCode() ?? 0;

    /// <summary>Equality operator.</summary>
    public static bool operator ==(SystemEventReader left, SystemEventReader right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(SystemEventReader left, SystemEventReader right) => !left.Equals(right);
}
