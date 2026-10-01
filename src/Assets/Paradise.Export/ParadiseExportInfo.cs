#nullable enable
using System.Text.Json;
using DotRecast.Detour.Io;

namespace Paradise.Export
{
    /// <summary>Identifies the export assembly when an editor plugin loads.</summary>
    /// <remarks>Avoids serializer caches that can pin Godot's collectible assemblies (godotengine/godot#78513).</remarks>
    public static class ParadiseExportInfo
    {
        public const string Version = "0.1.0";

        public static string Describe()
        {
            string? systemTextJson = typeof(JsonSerializer).Assembly.GetName().Version?.ToString();
            string? dotRecast = typeof(DtMeshSetWriter).Assembly.GetName().Version?.ToString();
            return $"{{\"tool\":\"Paradise.Export\",\"version\":{JsonString(Version)}," +
                   $"\"systemTextJson\":{JsonString(systemTextJson)},\"dotRecast\":{JsonString(dotRecast)}}}";
        }

        // Quotes the known assembly/version strings without invoking a serializer; this helper
        // escapes quotes and backslashes, but is not a general control-character encoder.
        private static string JsonString(string? value) =>
            value is null ? "null" : $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }
}
