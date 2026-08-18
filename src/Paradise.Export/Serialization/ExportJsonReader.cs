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

        public static LevelData ReadLevel(string json) => Deserialize<LevelData>(json);

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
