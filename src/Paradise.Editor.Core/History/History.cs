using Paradise.Editor.Core.Document;
using Zio;

namespace Paradise.Editor.Core.History;

/// <summary>The reference <see cref="IHistory"/>: versions and file operations in one list.</summary>
/// <remarks>Deliberately without a byte budget or grouping yet; those are the first things E1
/// adds, and the shape here is what they extend.</remarks>
public sealed class History(SceneDocument initial, IFileSystem fileSystem) : IHistory
{
    private readonly SceneDocument _initial = initial;
    private readonly List<IHistoryEntry> _entries = [];
    private int _cursor;

    public SceneDocument Current { get; private set; } = initial;

    public bool CanUndo => _cursor > 0;

    public bool CanRedo => _cursor < _entries.Count;

    public void Commit(IHistoryEntry entry)
    {
        _entries.RemoveRange(_cursor, _entries.Count - _cursor);
        _entries.Add(entry);
        _cursor = _entries.Count;
        if (entry is DocumentVersion version) Current = version.Document;
    }

    public IHistoryEntry? Undo()
    {
        if (!CanUndo) return null;
        var entry = _entries[--_cursor];
        switch (entry)
        {
            case DocumentVersion:
                Current = LatestDocumentBefore(_cursor) ?? _initial;
                break;
            case IReversibleFileOperation operation:
                operation.Revert(fileSystem);
                break;
        }
        return entry;
    }

    public IHistoryEntry? Redo()
    {
        if (!CanRedo) return null;
        var entry = _entries[_cursor++];
        switch (entry)
        {
            case DocumentVersion version:
                Current = version.Document;
                break;
            case IReversibleFileOperation operation:
                operation.Reapply(fileSystem);
                break;
        }
        return entry;
    }

    private SceneDocument? LatestDocumentBefore(int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (_entries[i] is DocumentVersion version) return version.Document;
        }
        return null;
    }
}
