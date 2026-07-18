#nullable enable
using Paradise.Export.Data;

namespace Paradise.Export.Serialization
{
    /// <summary>
    /// Serialization seam for exported scene documents. The scene exporter builds an
    /// engine-neutral <see cref="LevelData"/> and hands it to a writer, decoupling the
    /// exporter from the on-disk format.
    ///
    /// Today the only implementation is <see cref="JsonSceneDocumentWriter"/> (JSON). A
    /// future BLOB/ECS writer can be injected without changing any exporter code: each DTO
    /// that maps to a Paradise Engine ECS component carries a
    /// <see cref="ParadiseComponentAttribute"/> with a stable component GUID.
    /// </summary>
    public interface ISceneDocumentWriter
    {
        void Write(string outputPath, LevelData document);
    }
}
