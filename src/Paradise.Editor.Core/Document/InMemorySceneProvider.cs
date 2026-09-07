namespace Paradise.Editor.Core.Document;

/// <summary>A scene held in memory and nowhere else.</summary>
/// <remarks>What the standalone host edits before a project is open, and what a test uses. E3
/// replaces it with a file-backed provider over <c>assets/</c>; nothing above this line changes
/// when it does, which is the point of the seam.</remarks>
public sealed class InMemorySceneProvider(SceneDocument? initial = null, bool canAccept = true) : ISceneProvider
{
    public SceneDocument Current { get; private set; } = initial ?? SceneDocument.Empty;

    public bool CanAccept => canAccept;

    public event Action<SceneDocument, SceneDocument>? Changed;

    public void Accept(SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var previous = Current;
        Current = document;
        Changed?.Invoke(previous, document);
    }
}
