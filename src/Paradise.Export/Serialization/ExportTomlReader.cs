#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Paradise.Export.Data;

using Tomlyn;
using Tomlyn.Model;

namespace Paradise.Export.Serialization
{
    /// <summary>
    /// The read half of the contract's TOML form, mirroring <see cref="ExportJsonReader"/>.
    /// </summary>
    /// <remarks>
    /// Parses to Tomlyn's untyped model, converts to a node tree, and deserializes with
    /// <see cref="ExportJsonReader"/>'s own options — the same source-generated resolver and the
    /// same converters the JSON path uses, so a document read either way yields the same value.
    /// Reflection-free, and AOT-clean on both halves.
    /// </remarks>
    public static class ExportTomlReader
    {
        /// <summary>Reads a level document.</summary>
        public static PrefabData ReadLevel(string toml) => Read<PrefabData>(toml);

        /// <summary>Reads a material document.</summary>
        public static LevelMaterialData ReadMaterial(string toml) => Read<LevelMaterialData>(toml);

        /// <summary>Reads the project settings document.</summary>
        public static ProjectSettingsData ReadProjectSettings(string toml) => Read<ProjectSettingsData>(toml);

        /// <summary>
        /// The document as the contract's JSON text — for a reader that works in JSON and only
        /// needs the format bridged.
        /// </summary>
        /// <remarks>
        /// <see cref="Data.AuthoredDocument"/> is the case this exists for: it reads component
        /// payloads as <c>JsonElement</c> and is shared by the schema, the config and the live
        /// protocol. Bridging the TEXT lets one reader serve both formats, rather than a second
        /// traversal of the same document growing beside the first.
        /// </remarks>
        public static string ToJsonText(string toml) => ParseTable(toml).ToJsonString();

        /// <summary>Reads any contract document.</summary>
        public static T Read<T>(string toml)
        {
            var node = ParseTable(toml);
            var typeInfo = (JsonTypeInfo<T>)ExportJsonReader.SerializerOptions.GetTypeInfo(typeof(T));
            return JsonSerializer.Deserialize(node, typeInfo)
                ?? throw new InvalidDataException("the document is valid TOML but describes nothing");
        }

        /// <summary>Parses TOML into the contract's node tree.</summary>
        private static JsonNode ParseTable(string toml)
        {
            ArgumentNullException.ThrowIfNull(toml);

            TomlTable table;
            try
            {
                table = TomlSerializer.Deserialize<TomlTable>(toml, UntypedTomlContext.Default)
                    ?? new TomlTable();
            }
            catch (Exception error) when (error is TomlException or InvalidOperationException)
            {
                throw new InvalidDataException($"the document is not valid TOML: {error.Message}", error);
            }

            return TomlJsonBridge.ToJson(table);
        }
    }
}
