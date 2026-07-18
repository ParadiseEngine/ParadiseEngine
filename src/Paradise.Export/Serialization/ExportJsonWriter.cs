#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Paradise.Export.Data;
using Paradise.Export.Serialization.Converters;

namespace Paradise.Export.Serialization
{
    /// <summary>
    /// Serializes exported documents (scenes, materials, prefabs, project settings) with
    /// System.Text.Json. Uses source-generated metadata (<see cref="ParadiseJsonContext"/>) plus
    /// hand-written converters for the System.Numerics vector/matrix shapes and Color32 — so the
    /// output is the contract's shape (vectors/matrices as float arrays, matrices column-major,
    /// Color32 as { r, g, b, a }, enums by name, nulls included), without any reflection-based
    /// serializer that would pin Godot's collectible AssemblyLoadContext (godotengine/godot#78513).
    ///
    /// Note: numeric formatting is STJ-native (e.g. <c>5</c> not <c>5.0</c>) — the export contract is
    /// value-based, not byte-based. Writes are atomic (temp file + rename).
    /// </summary>
    public static class ExportJsonWriter
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                Converters =
                {
                    new Color32Converter(),
                    new Vector2Converter(),
                    new Vector3Converter(),
                    new Vector4Converter(),
                    new QuaternionConverter(),
                    new Matrix4x4Converter(),
                    // NOTE: any NEW data-model enum that must serialize by name needs its own
                    // JsonStringEnumConverter<T> entry here (or a [JsonConverter] on the enum);
                    // otherwise STJ silently writes it as an integer.
                    new JsonStringEnumConverter<PhysicsBodyType>(),
                    new JsonStringEnumConverter<PhysicsShapeType>(),
                    new JsonStringEnumConverter<ParticleRenderKind>(),
                },
            };
            options.TypeInfoResolverChain.Add(ParadiseJsonContext.Default);
            return options;
        }

        public static void WriteJsonDocument(string outputPath, object document)
        {
            string json = SerializeToString(document) + Environment.NewLine;
            WriteTextAtomically(outputPath, json);
        }

        public static string SerializeToString(object document)
        {
            // NOTE: the runtime type must be registered as a [JsonSerializable] root in
            // ParadiseJsonContext — GetTypeInfo throws (not a compile error) for unregistered types.
            JsonTypeInfo typeInfo = Options.GetTypeInfo(document.GetType());
            return JsonSerializer.Serialize(document, typeInfo);
        }

        public static void WriteTextAtomically(string outputPath, string text)
        {
            string directory = Path.GetDirectoryName(outputPath) ?? ".";
            Directory.CreateDirectory(directory);
            string tempPath = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                File.WriteAllText(tempPath, text);
                if (File.Exists(outputPath))
                {
                    File.Replace(tempPath, outputPath, null);
                }
                else
                {
                    File.Move(tempPath, outputPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
    }
}
