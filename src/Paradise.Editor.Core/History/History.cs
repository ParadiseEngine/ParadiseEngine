using Paradise.Editor.Core.Document;
using Zio;

namespace Paradise.Editor.Core.History;

/// <summary>The reference <see cref="IHistory"/>: versions and file operations in one list,
/// published through the scene provider.</summary>
/// <remarks>
/// <para>
/// Every document step goes out through <see cref="ISceneProvider.Accept"/>, which is what makes
/// one history serve both hosts: standalone the provider holds the file's document, in-game it
/// applies the version to the live world. Nothing here keeps its own copy to diverge from that.
/// </para>
/// <para>
/// Deliberately without a byte budget or grouping yet; those are the first things E1 adds, and
/// the shape here is what they extend.
/// </para>
/// </remarks>
public sealed class History(ISceneProvider scene, IFileSystem fileSystem) : IHistory
{
    // Read through the field, never the parameter: a primary-constructor parameter used BOTH in a
    // field initializer and in a method body is CS9124.
    private readonly ISceneProvider _scene = scene;
    private readonly SceneDocument _initial = scene.Current;
    private readonly List<IHistoryEntry> _entries = [];
    private int _cursor;

    public bool CanUndo => _cursor > 0;

    public bool CanRedo => _cursor < _entries.Count;

    public void Commit(IHistoryEntry entry)
    {
        // Published BEFORE the entry is recorded: a provider that refuses must leave the history
        // exactly as it was, not holding a step that never happened.
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

        // Moved only once the side effect succeeded. A Revert that throws — the file deleted
        // outside the editor, a read-only mount — would otherwise leave the cursor past a step
        // that is still applied, and the next Redo would Reapply something never reverted.
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
