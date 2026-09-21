#nullable enable
using Paradise.Export.Data;

namespace Paradise.Export.Serialization
{
    /// <summary>Atomically writes scene JSON through <see cref="ExportJsonWriter"/>.</summary>
    public sealed class JsonSceneDocumentWriter : ISceneDocumentWriter
    {
        public void Write(string outputPath, PrefabData document)
        {
            ExportJsonWriter.WriteJsonDocument(outputPath, document);
        }
    }
}
