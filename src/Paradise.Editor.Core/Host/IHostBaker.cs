using Paradise.Editor.Core.Document;

namespace Paradise.Editor.Core.Host;

/// <summary>Resolves the editor's host references into the values the asset pipeline expects.</summary>
/// <remarks>The bake is the host's job, not the pipeline's: every host hands the pipeline the same
/// resolved shape, and the pipeline stays ignorant of who authored it. Play runs this before the
/// dev-profile build into <c>.editor/play/</c>.</remarks>
public interface IHostBaker
{
    SceneDocument Bake(SceneDocument authored);
}
