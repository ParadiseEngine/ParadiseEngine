namespace Paradise.Editor.Core.Extensibility;

/// <summary>An owner-scoped list of contributions of one kind.</summary>
public interface IRegistry<T>
{
    IReadOnlyList<T> Entries { get; }

    void Add(OwnerToken owner, T entry);

    void RemoveOwner(OwnerToken owner);
}

/// <summary>An insertion-ordered registry with removal by owner.</summary>
/// <remarks>Caches snapshots until registration changes; existing enumerations remain valid during removal.</remarks>
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
