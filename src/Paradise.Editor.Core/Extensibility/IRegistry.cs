namespace Paradise.Editor.Core.Extensibility;

/// <summary>An owner-scoped list of contributions of one kind.</summary>
public interface IRegistry<T>
{
    IReadOnlyList<T> Entries { get; }

    void Add(OwnerToken owner, T entry);

    void RemoveOwner(OwnerToken owner);
}

/// <summary>The reference <see cref="IRegistry{T}"/>: insertion order, removal by owner.</summary>
/// <remarks>The projection is cached and invalidated on change rather than rebuilt per read.
/// <see cref="Entries"/> is on frame paths — one walk per inspector row, one per dispatch — and
/// registration happens at startup or when an extension loads, so the allocation belongs there.
/// Handing out a snapshot rather than a live view is also what makes an extension unloading
/// itself mid-frame safe: an enumeration already in flight finishes over the array it started
/// on.</remarks>
public sealed class Registry<T> : IRegistry<T>
{
    private readonly List<(OwnerToken Owner, T Entry)> _entries = [];
    private T[]? _snapshot;

    public IReadOnlyList<T> Entries => _snapshot ??= _entries.Select(pair => pair.Entry).ToArray();

    public void Add(OwnerToken owner, T entry)
    {
        _entries.Add((owner, entry));
        _snapshot = null;
    }

    public void RemoveOwner(OwnerToken owner)
    {
        if (_entries.RemoveAll(pair => pair.Owner == owner) > 0) _snapshot = null;
    }
}
