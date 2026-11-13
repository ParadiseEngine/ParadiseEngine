#nullable enable
using System.Reflection;
using System.Text.Json;
using DotRecast.Detour.Io;

namespace Paradise.Export
{
    /// <summary>Identifies the export assembly when an editor plugin loads.</summary>
    /// <remarks>Avoids serializer caches that can pin Godot's collectible assemblies (godotengine/godot#78513).</remarks>
    public static class ParadiseExportInfo
    {
        /// <summary>The engine version this export assembly was built as, without build metadata.</summary>
        /// <remarks>Read from the assembly so it follows the package version instead of a hand-kept constant.</remarks>
        public static string Version { get; } = ReadVersion();

        private static string ReadVersion()
        {
            string? informational = typeof(ParadiseExportInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(informational))
                return typeof(ParadiseExportInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            int metadata = informational!.IndexOf('+');
            return metadata < 0 ? informational : informational.Substring(0, metadata);
        }

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
