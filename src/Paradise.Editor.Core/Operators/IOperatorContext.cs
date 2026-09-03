using Microsoft.Extensions.Logging;
using Paradise.Editor.Core.Document;
using Paradise.Editor.Core.History;

namespace Paradise.Editor.Core.Operators;

/// <summary>What the host can do, so operators and menus stay host-agnostic.</summary>
/// <remarks>Standalone: edits documents, bakes, plays a child process. In-game: the game decides
/// whether the live world accepts documents, and play is the game itself.</remarks>
public interface IHostCapabilities
{
    bool CanEditDocument { get; }

    bool CanBake { get; }

    bool CanPlayChildProcess { get; }
}

/// <summary>Everything an operator may read, and the two ways it may write.</summary>
public interface IOperatorContext
{
    SceneDocument Document { get; }

    Selection.Selection Selection { get; }

    IHistory History { get; }

    IHostCapabilities Host { get; }

    /// <summary>Where an operator says what happened. Never nullable: <c>[LoggerMessage]</c> calls
    /// <c>IsEnabled</c> unguarded, so a null one fails to compile inside generated code — a host
    /// with nothing to say installs <c>NullLogger.Instance</c>.</summary>
    ILogger Log { get; }

    /// <summary>Publish a new document version as one undoable step.</summary>
    void Commit(SceneDocument document, string description);

    void Select(Selection.Selection selection);
}
