#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Paradise.Export.Data;
using Paradise.Export.Serialization.Converters;

namespace Paradise.Export.Serialization
{
    /// <summary>
    /// The read half of the contract: deserializes exported documents with the same
    /// source-generated metadata + converters <see cref="ExportJsonWriter"/> writes with, so the
    /// round trip is exact. Consumed by runtimes (Paradise.Sample.Runtime) that load <c>data/</c> —
    /// reflection-free, AOT-clean.
    /// </summary>
    public static class ExportJsonReader
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

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
                    new JsonStringEnumConverter<ParticleRenderKind>(),
                },
            };
            options.TypeInfoResolverChain.Add(ParadiseJsonContext.Default);
            return options;
        }

        /// <summary>
        /// Deserialize an ALREADY-PARSED element with the contract's converters.
        ///
        /// Exists because <c>ParadiseJsonContext.Default</c> on its own is not enough: the
        /// converters that make enums travel by name and vectors travel as float arrays live in
        /// these options, not in the generated context. Deserializing a component payload without
        /// them silently fails on the first enum or Vector3 — which is exactly how
        /// <see cref="Data.AuthoredComponentRouter"/> would lose authored data.
        /// </summary>
        internal static T? ReadElement<T>(JsonElement element) where T : class =>
            element.Deserialize((JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)));

        /// <summary>
        /// Read a level document, refusing one this build cannot understand.
        ///
        /// The gate arrived with v3 and earns its keep again at v4, because BOTH breaks fail
        /// quietly without it — in opposite ways, which is the point:
        ///
        /// - a v2 document carries <c>"Components"</c> as an OBJECT of named slots where this
        ///   build expects an array, so it throws a raw JsonException naming a token position, or
        ///   parses into entities with no components at all — "the scene authored nothing";
        /// - a v3 document parses PERFECTLY. Its entity-level <c>"Materials"</c> simply matches no
        ///   property any more and is dropped by the deserializer, so the scene loads with every
        ///   mesh in place and every material override gone. Nothing anywhere reports it; the
        ///   district just renders in the GLBs' own colours.
        ///
        /// The second is the more dangerous, and it is why v3 is refused despite BEING
        /// mechanically convertible: <c>tools/migrate_level_v3_to_v4.py</c> does it in one pass,
        /// and re-exporting from the editor is better still.
        /// </summary>
        public static LevelData ReadLevel(string json)
        {
            // The version is read BEFORE the body, not after. A v2 document does not survive
            // deserialization far enough to be asked its version: its "Components" is an object
            // where this build expects an array, so STJ throws first, naming a token position and
            // nothing about why. Peeking costs one parse of a small prefix and buys an error that
            // says what to do.
            using (JsonDocument peek = JsonDocument.Parse(json))
            {
                int version = peek.RootElement.TryGetProperty("SchemaVersion", out JsonElement element)
                    && element.TryGetInt32(out int parsed)
                        ? parsed
                        : LevelData.CurrentSchemaVersion;
                if (version < LevelData.MinimumSupportedVersion ||
                    version > LevelData.CurrentSchemaVersion)
                {
                    throw new JsonException(
                        $"Level document is schema version {version}; this build reads "
                        + $"{LevelData.MinimumSupportedVersion}..{LevelData.CurrentSchemaVersion}. "
                        + "Re-export the scene from its editor. A v3 document can also be "
                        + "converted in place with tools/migrate_level_v3_to_v4.py, which moves "
                        + "each entity's Materials onto its Renderable component; v2 and older "
                        + "have no upgrade path and must be re-exported.");
                }
            }
            return Deserialize<LevelData>(json);
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
