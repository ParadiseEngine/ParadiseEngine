using Paradise.Editor.Core.Document;
using Zio;

namespace Paradise.Editor.Core.History;

/// <summary>One undoable step. Two shapes exist and they revert differently.</summary>
public interface IHistoryEntry
{
    string Description { get; }
}

/// <summary>A document edit: the whole document after the step. Undo republishes the version
/// before it; there is no inverse to write, so no inverse can be wrong.</summary>
public sealed record DocumentVersion(SceneDocument Document, string Description) : IHistoryEntry;

/// <summary>A change to <c>assets/</c> that happened on disk the moment it ran: move, rename,
/// delete, import, mint a sidecar.</summary>
/// <remarks>A document snapshot cannot revert these, so each carries its own inverse: a move
/// moves back and rewrites the references it rewrote, a delete restores from the editor's trash.
/// The set is small and every inverse is well defined, which is why a journal beats a VCS here.</remarks>
public interface IReversibleFileOperation : IHistoryEntry
{
    void Revert(IFileSystem fileSystem);

    void Reapply(IFileSystem fileSystem);
}

/// <summary>A linear list of steps with a cursor; document steps and file steps share it so
/// undo is one stream.</summary>
/// <remarks>There is no <c>Current</c> here on purpose. The live document has exactly one owner,
/// the <see cref="ISceneProvider"/>, and history publishes to it — otherwise the in-game host has
/// two copies and undo moves only the one the editor happens to read.</remarks>
public interface IHistory
{
    bool CanUndo { get; }

    bool CanRedo { get; }

    void Commit(IHistoryEntry entry);

    IHistoryEntry? Undo();

    IHistoryEntry? Redo();
}
