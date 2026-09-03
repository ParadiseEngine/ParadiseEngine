using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Paradise.Editor.Core.Document;
using Paradise.Editor.Core.History;
using Paradise.Editor.Core.Operators;
using Zio;
// Aliased because the History PROPERTY below shadows the namespace of the same name.
using HistoryList = Paradise.Editor.Core.History.History;
// Same shadowing, for the Selection property.
using SelectionSet = Paradise.Editor.Core.Selection.Selection;

namespace Paradise.Editor.Core;

/// <summary>What the host can do, as a value.</summary>
public sealed record HostCapabilities(bool CanEditDocument, bool CanBake, bool CanPlayChildProcess)
    : IHostCapabilities
{
    /// <summary>The standalone editor: it owns the files, so it can do everything.</summary>
    public static HostCapabilities Standalone { get; } = new(true, true, true);

    /// <summary>An editor embedded in a running game: it edits the live world, and play is the
    /// game itself rather than a child process.</summary>
    public static HostCapabilities InGame { get; } = new(true, false, false);
}

/// <summary>One editing session: the document, its history, the selection, and what the host can
/// do with them.</summary>
/// <remarks>The concrete <see cref="IOperatorContext"/> both hosts use. It holds no UI and no
/// files of its own — the provider decides where the document comes from and the filesystem is the
/// one file operations revert through — which is why the same type serves a standalone editor over
/// <c>assets/</c> and an in-game editor over a live world.</remarks>
public sealed class EditorSession : IOperatorContext
{
    private readonly ISceneProvider _scene;

    public EditorSession(
        ISceneProvider scene,
        IFileSystem fileSystem,
        IHostCapabilities? host = null,
        ILogger? log = null,
        long historyBudgetBytes = HistoryList.DefaultBudgetBytes)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _scene = scene;
        History = new HistoryList(scene, fileSystem, historyBudgetBytes);
        Host = host ?? HostCapabilities.Standalone;
        Log = log ?? NullLogger.Instance;
    }

    public SceneDocument Document => _scene.Current;

    public SelectionSet Selection { get; private set; } = SelectionSet.Empty;

    public IHistory History { get; }

    public IHostCapabilities Host { get; }

    public ILogger Log { get; }

    public void Commit(SceneDocument document, string description) =>
        History.Commit(new DocumentVersion(document, description));

    /// <summary>Selection is editor state, never a history step — undoing an edit must not also
    /// undo what the user had clicked on since.</summary>
    public void Select(SelectionSet selection) => Selection = selection;
}
