#nullable enable
using System;
using System.IO;
using System.Text.Json;
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
        public static LevelData ReadLevel(string toml) => Read<LevelData>(toml);

        /// <summary>Reads any contract document.</summary>
        public static T Read<T>(string toml)
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

            var node = TomlJsonBridge.ToJson(table);
            var typeInfo = (JsonTypeInfo<T>)ExportJsonReader.SerializerOptions.GetTypeInfo(typeof(T));
            return JsonSerializer.Deserialize(node, typeInfo)
                ?? throw new InvalidDataException("the document is valid TOML but describes nothing");
        }
    }
}
