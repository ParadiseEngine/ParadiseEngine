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
        /// The gate earns its keep again at v5, and for the reason it was added: the break is
        /// SILENT without it. A v4 document deserializes perfectly here — its entities are
        /// objects, not arrays, so <c>Entities</c> parses as… nothing, and the scene loads as an
        /// empty world with no error anywhere. Worse, a hand-written v4-shaped entity whose
        /// components happen to parse would load every component and lose its name, its
        /// transform and its active flag in silence, because those are not properties of anything
        /// any more.
        ///
        /// There is no shim and no migration script. A v4 document does not CONTAIN a v5
        /// document's information: the name and the world matrix are recoverable, but which
        /// objects were switched off, and which of the eighteen entity fields a given host meant,
        /// are not decisions a converter can make. Re-export the scene from its editor.
        /// </summary>
        public static PrefabData ReadPrefab(string json)
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
                        : PrefabData.CurrentSchemaVersion;
                if (version < PrefabData.MinimumSupportedVersion ||
                    version > PrefabData.CurrentSchemaVersion)
                {
                    throw new JsonException(
                        $"Level document is schema version {version}; this build reads "
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
