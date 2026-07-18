#nullable enable
using System.Text.Json.Serialization;
using Paradise.Export.Data;

namespace Paradise.Export.Serialization
{
    /// <summary>
    /// System.Text.Json source-generated metadata for the exported document roots. Source generation
    /// keeps serialization reflection-free (AOT-compatible) and, importantly, free of the static
    /// reflection caches that made Newtonsoft.Json pin Godot's collectible AssemblyLoadContext and
    /// break C# hot-reload (godotengine/godot#78513).
    ///
    /// The System.Numerics / Color32 shapes and enum-by-name are supplied by the converters added in
    /// <see cref="ExportJsonWriter"/>'s options (the net8 source-gen attribute can't register
    /// converter instances directly).
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
    [JsonSerializable(typeof(LevelData))]
    [JsonSerializable(typeof(ProjectSettingsData))]
    [JsonSerializable(typeof(LevelMaterialData))]
    [JsonSerializable(typeof(PrefabTemplateData))]
    internal sealed partial class ParadiseJsonContext : JsonSerializerContext
    {
    }
}
