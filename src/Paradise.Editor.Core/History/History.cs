using Paradise.Editor.Core.Document;
using Zio;

namespace Paradise.Editor.Core.History;

/// <summary>Tracks document versions and reversible file operations for undo and redo.</summary>
/// <remarks>Publishes versions through <see cref="ISceneProvider.Accept"/> for both file-backed and live-world hosts.</remarks>
public sealed class History(ISceneProvider scene, IFileSystem fileSystem) : IHistory
{
    // Keep method access on the field to avoid primary-constructor double capture (CS9124).
    private readonly ISceneProvider _scene = scene;
    private readonly SceneDocument _initial = scene.Current;
    private readonly List<IHistoryEntry> _entries = [];
    private int _cursor;

    public bool CanUndo => _cursor > 0;

    public bool CanRedo => _cursor < _entries.Count;

    public void Commit(IHistoryEntry entry)
    {
        // Publish first so a rejected version leaves history unchanged.
        if (entry is DocumentVersion version) Publish(version.Document);

        _entries.RemoveRange(_cursor, _entries.Count - _cursor);
        _entries.Add(entry);
        _cursor = _entries.Count;
    }

    public IHistoryEntry? Undo()
    {
        if (!CanUndo) return null;
        var index = _cursor - 1;
        var entry = _entries[index];
        switch (entry)
        {
            case DocumentVersion:
                Publish(LatestDocumentBefore(index) ?? _initial);
                break;
            case IReversibleFileOperation operation:
                operation.Revert(fileSystem);
                break;
        }

        // Advance the cursor only after the operation succeeds.
        _cursor = index;
        return entry;
    }

    public IHistoryEntry? Redo()
    {
        if (!CanRedo) return null;
        var entry = _entries[_cursor];
        switch (entry)
        {
            case DocumentVersion version:
                Publish(version.Document);
                break;
            case IReversibleFileOperation operation:
                operation.Reapply(fileSystem);
                break;
        }

        _cursor++;
        return entry;
    }

    private void Publish(SceneDocument document)
    {
        if (!_scene.CanAccept)
        {
            throw new InvalidOperationException(
                "The scene provider is read-only. A host reporting CanAccept false must expose no "
                + "operators that commit a document version.");
        }
        _scene.Accept(document);
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
