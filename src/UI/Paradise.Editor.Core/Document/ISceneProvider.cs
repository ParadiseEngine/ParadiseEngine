namespace Paradise.Editor.Core.Document;

/// <summary>Where the current scene comes from and where a committed one goes.</summary>
/// <remarks>The seam that lets one editor run in two hosts. Standalone, the provider is a
/// file: the document is loaded from <c>assets/</c> and written back on explicit save. In-game,
/// the provider is the live world: the document is projected from it and accepting a version
/// applies it back, which the game implements. Undo works identically over both, because it only
/// ever asks the provider to accept an earlier version.</remarks>
public interface ISceneProvider
{
    SceneDocument Current { get; }

    /// <summary>Whether <see cref="Accept"/> is supported; a read-only projection reports false
    /// and the host then exposes no editing operators.</summary>
    bool CanAccept { get; }

    /// <summary>Raised after <see cref="Current"/> changed, with the previous and the new version
    /// so an observer can diff them by reference.</summary>
    event Action<SceneDocument, SceneDocument>? Changed;

    void Accept(SceneDocument document);
}
