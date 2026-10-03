using Paradise.Editor.Core.Document;

namespace Paradise.Editor.Core.Host;

/// <summary>Resolves the editor's host references into the values the asset pipeline expects.</summary>
/// <remarks>The bake is the host's job, not the pipeline's: every host hands the pipeline the same
/// resolved shape, and the pipeline stays ignorant of who authored it. A host should bake before
/// passing the document to its play build.</remarks>
public interface IHostBaker
{
    SceneDocument Bake(SceneDocument authored);
}
