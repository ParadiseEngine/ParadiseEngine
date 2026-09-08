using System;

using Paradise.Export.Data;

namespace Paradise.Export.Serialization
{
    /// <summary>Reads built TOML or JSON documents by inspecting their contents.</summary>
    /// <remarks>Built materials retain <c>.material</c> in every profile; an opening brace identifies JSON.</remarks>
    public static class ExportDocumentReader
    {
        /// <summary>Reads a material document as TOML or JSON, whichever the text is.</summary>
        public static LevelMaterialData ReadMaterial(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            return IsJson(text) ? ExportJsonReader.ReadMaterial(text) : ExportTomlReader.ReadMaterial(text);
        }

        /// <summary>Whether the text is a JSON document: it opens with a brace. TOML cannot.</summary>
        public static bool IsJson(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            var trimmed = text.AsSpan().TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '\uFEFF') trimmed = trimmed[1..].TrimStart();
            return trimmed.Length > 0 && trimmed[0] == '{';
        }
    }
}
