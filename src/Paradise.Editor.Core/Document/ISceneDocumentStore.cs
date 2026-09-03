using Zio;

namespace Paradise.Editor.Core.Document;

/// <summary>Reads and writes a scene through the mount the host provides; never a host path.</summary>
/// <remarks>
/// <para>
/// The same shape the runtime's own reader takes (<c>AuthoredDocument.Load</c>), and for the same
/// reasons: a <see cref="UPath"/> is '/'-separated on every platform, which is exactly how the
/// asset contract spells a field, and containment is the mount's job rather than a check written
/// here. The editor mounts its project, a shipped build mounts an archive, a test mounts memory.
/// </para>
/// <para>
/// <c>path</c> must be ABSOLUTE in <c>fileSystem</c>. A UPath is rooted at
/// the mount, not at a working directory, so a relative one names nothing — the runtime reader
/// refuses it rather than resolving it against something it cannot know, and an editor that
/// accepted it would write the file somewhere else than the runtime later looks.
/// </para>
/// </remarks>
public interface ISceneDocumentStore
{
    SceneDocument Load(IFileSystem fileSystem, UPath path);

    void Save(IFileSystem fileSystem, UPath path, SceneDocument document);
}
