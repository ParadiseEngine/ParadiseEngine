using Zio;

namespace Paradise.Editor.Core.Document;

/// <summary>Reads and writes a scene through the mount the host provides; never a host path.</summary>
public interface ISceneDocumentStore
{
    SceneDocument Load(IFileSystem fileSystem, UPath path);

    void Save(IFileSystem fileSystem, UPath path, SceneDocument document);
}
