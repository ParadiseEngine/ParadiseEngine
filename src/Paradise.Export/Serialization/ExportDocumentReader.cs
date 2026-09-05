using System;

using Paradise.Export.Data;

namespace Paradise.Export.Serialization
{
    /// <summary>
    /// Reads a built document whose NAME does not say its format. A built <c>.material</c> keeps
    /// that suffix under every build profile and carries TOML or JSON by profile, so the text has
    /// to say which: JSON is the only one of the two that can open with a brace.
    /// </summary>
    /// <remarks>
    /// One sniff for every host, rather than one per host: the rule is trivial, but a host that
    /// dispatches on extension instead reads the file as the wrong format and reports a parse
    /// error naming nothing. Built prefabs and settings still carry the format in their extension
    /// and have no need of this.
    /// </remarks>
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
