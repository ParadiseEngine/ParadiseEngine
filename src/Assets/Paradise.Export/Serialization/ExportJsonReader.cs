#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Paradise.Export.Data;
using Paradise.Export.Serialization.Converters;

namespace Paradise.Export.Serialization
{
    /// <summary>Reads exported JSON with the writer's source-generated metadata and converters.</summary>
    public static class ExportJsonReader
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        /// <summary>The contract's own options, shared with the TOML path.</summary>
        /// <remarks>
        /// Exposed so <see cref="ExportTomlWriter"/> and <see cref="ExportTomlReader"/> serialize
        /// through the SAME converters and source-generated resolver. A second set of options would
        /// be a second contract, and nothing would notice the day they disagreed.
        /// </remarks>
        internal static JsonSerializerOptions SerializerOptions => Options;

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                Converters =
                {
                    new Color32Converter(),
                    new Vector2Converter(),
                    new Vector3Converter(),
                    new Vector4Converter(),
                    new QuaternionConverter(),
                    new Matrix4x4Converter(),
                    new JsonStringEnumConverter<PhysicsBodyType>(),
                    new JsonStringEnumConverter<PhysicsShapeType>(),
                },
            };
            options.TypeInfoResolverChain.Add(ParadiseJsonContext.Default);
            return options;
        }

        /// <summary>
        /// Deserializes an already-parsed element into a registered contract type using the contract's converters.
        /// </summary>
        internal static T? ReadElement<T>(JsonElement element) where T : class =>
            element.Deserialize((JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)));

        /// <summary>
        /// Reads a prefab after checking its schema version against the supported range.
        /// </summary>
        /// <remarks>Earlier formats used different entity or placement contracts; unsupported documents must be re-exported.</remarks>
        public static PrefabData ReadPrefab(string json)
        {
            // Parse the JSON once to check the version before typed deserialization, so an old
            // document reports its incompatible contract instead of a misleading shape error.
            using (JsonDocument peek = JsonDocument.Parse(json))
            {
                int version = peek.RootElement.TryGetProperty("SchemaVersion", out JsonElement element)
                    && element.TryGetInt32(out int parsed)
                        ? parsed
                        : PrefabData.CurrentSchemaVersion;
                if (version < PrefabData.MinimumSupportedVersion ||
                    version > PrefabData.CurrentSchemaVersion)
                {
                    throw new JsonException(
                        $"Prefab document is schema version {version}; this build reads "
                        + $"{PrefabData.MinimumSupportedVersion}..{PrefabData.CurrentSchemaVersion}. "
                        + "Re-export the scene from its editor: v5 made an object nothing but its "
                        + "authored components, and no earlier document carries enough to be "
                        + "converted into one.");
                }
            }
            return Deserialize<PrefabData>(json);
        }

        public static LevelMaterialData ReadMaterial(string json) => Deserialize<LevelMaterialData>(json);

        public static ProjectSettingsData ReadProjectSettings(string json) => Deserialize<ProjectSettingsData>(json);

        private static T Deserialize<T>(string json)
        {
            var typeInfo = (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
            return JsonSerializer.Deserialize(json, typeInfo)
                ?? throw new JsonException($"{typeof(T).Name} document deserialized to null.");
        }
    }
}
