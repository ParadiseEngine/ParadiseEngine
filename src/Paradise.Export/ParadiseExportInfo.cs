#nullable enable
using System.Text.Json;
using DotRecast.Detour.Io;

namespace Paradise.Export
{
    /// <summary>
    /// Small identity string logged when the editor plugin loads. Deliberately built without a JSON
    /// serializer — invoking one at editor-load would warm serializer caches that hinder Godot's
    /// C# assembly hot-reload (godotengine/godot#78513). Confirms the Core dependencies resolve.
    /// </summary>
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

        // Minimal JSON string encoder so the hand-built identity stays valid JSON without invoking a
        // serializer: null → bare `null`, otherwise a quoted, backslash/quote-escaped string.
        private static string JsonString(string? value) =>
            value is null ? "null" : $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }
}
