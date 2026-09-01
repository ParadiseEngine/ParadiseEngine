#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Tomlyn;
using Tomlyn.Model;

namespace Paradise.Export.Serialization
{
    /// <summary>
    /// The contract in TOML — the same documents <see cref="ExportJsonWriter"/> writes, in a form
    /// you can read and diff against <c>assets/</c>.
    /// </summary>
    /// <remarks>
    /// Serializes through <see cref="ExportJsonWriter"/>'s own options and converts the resulting
    /// node tree, so every converter and the contract's shape apply by construction. See
    /// <see cref="TomlJsonBridge"/> for why that indirection is the point rather than a shortcut,
    /// and for the one place the two formats differ (null).
    /// </remarks>
    public static class ExportTomlWriter
    {
        /// <summary>Serializes a document to TOML.</summary>
        public static string SerializeToString<T>(T document)
        {
            // GetTypeInfo + the JsonTypeInfo overload, NOT the options overload: the latter is
            // RequiresDynamicCode, and this package is deliberately reflection-free so a Godot
            // host's collectible AssemblyLoadContext is never pinned.
            var typeInfo = (JsonTypeInfo<T>)ExportJsonWriter.SerializerOptions.GetTypeInfo(typeof(T));
            var node = JsonSerializer.SerializeToNode(document, typeInfo);
            return TomlSerializer.Serialize(TomlJsonBridge.ToToml(node), UntypedTomlContext.Default);
        }

        /// <summary>Writes a document to <paramref name="outputPath"/>, atomically.</summary>
        public static void Write<T>(string outputPath, T document)
        {
            ArgumentException.ThrowIfNullOrEmpty(outputPath);

            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Temp-then-move, matching the JSON writer: a reader must never see half a document,
            // and a build that dies mid-write must not leave one.
            var temporary = outputPath + ".tmp";
            File.WriteAllText(temporary, SerializeToString(document));
            File.Move(temporary, outputPath, overwrite: true);
        }
    }

    /// <summary>Source-generated context for the untyped model, so the TOML path stays AOT-clean.</summary>
    [Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
    internal sealed partial class UntypedTomlContext : Tomlyn.Serialization.TomlSerializerContext;
}
