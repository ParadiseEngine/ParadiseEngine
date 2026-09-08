#nullable enable
using Paradise.Export.Data;

namespace Paradise.Export.Serialization
{
    /// <summary>Writes an engine-neutral <see cref="PrefabData"/> independently of its storage format.</summary>
    public interface ISceneDocumentWriter
    {
        void Write(string outputPath, PrefabData document);
    }
}
