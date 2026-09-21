using Paradise.Assets.Project;
using Paradise.Authoring;
using Paradise.Editor.Core.Document;
using Zio;

namespace Paradise.Editor.Core.Project;

/// <summary>An open project: its mounts, its schema, and the scene being edited.</summary>
/// <remarks><see cref="FileSystem"/> is the composed mount (<c>/assets</c>, <c>/cache</c>,
/// <c>/build</c>, <c>/play</c>); the editor never holds a host path. The schema is the file the
/// game's build writes, read as data. The editor writes to <c>/assets</c> only on explicit save
/// or an explicit file operation, and never reads an exported <c>data/</c> tree.</remarks>
public interface IProjectSession
{
    AssetProjectLayout Layout { get; }

    IFileSystem FileSystem { get; }

    AuthoringSchemaDocument? Schema { get; }

    ISceneProvider Scene { get; }
}
