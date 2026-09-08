#nullable enable
using System.Text.Json.Serialization;
using Paradise.Export.Data;

namespace Paradise.Export.Serialization
{
    /// <summary>Source-generated JSON metadata for exported document roots.</summary>
    /// <remarks>
    /// Avoids reflection caches that pin collectible editor assemblies (godotengine/godot#78513).
    /// <see cref="ExportJsonWriter"/> supplies the vector, matrix, Color32 and enum converters.
    /// </remarks>
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
    [JsonSerializable(typeof(PrefabData))]
    [JsonSerializable(typeof(ProjectSettingsData))]
    [JsonSerializable(typeof(LevelMaterialData))]
    [JsonSerializable(typeof(AuthoredComponentData))]
    // Every authored ENGINE component, so AuthoredComponentRouter can deserialize a payload into
    // its typed record without reflection.
    internal sealed partial class ParadiseJsonContext : JsonSerializerContext
    {
    }
}
