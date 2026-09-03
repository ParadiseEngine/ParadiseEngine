namespace Paradise.Editor.Core.Extensibility;

/// <summary>An owner-scoped list of contributions of one kind.</summary>
public interface IRegistry<T>
{
    IReadOnlyList<T> Entries { get; }

    void Add(OwnerToken owner, T entry);

    void RemoveOwner(OwnerToken owner);
}

/// <summary>The reference <see cref="IRegistry{T}"/>: insertion order, removal by owner.</summary>
public sealed class Registry<T> : IRegistry<T>
{
    private readonly List<(OwnerToken Owner, T Entry)> _entries = [];

    public IReadOnlyList<T> Entries => _entries.Select(pair => pair.Entry).ToArray();

    public void Add(OwnerToken owner, T entry) => _entries.Add((owner, entry));

    public void RemoveOwner(OwnerToken owner) => _entries.RemoveAll(pair => pair.Owner == owner);
}
