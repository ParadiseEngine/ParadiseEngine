using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paradise.Rendering;

/// <summary>
/// AOT/trim-safe <see cref="JsonSerializerContext"/> for the Slang-reflection-shaped records in
/// <see cref="ShaderProgramDesc"/> and friends. Snake-case-lower property naming targets the
/// Slang <c>-reflection-json</c> schema as of Slang v2026.7 (the version pinned by
/// <c>tools/slang/slang.manifest.json</c>, landing in #42). Enum values are PascalCase via
/// <see cref="JsonStringEnumConverter"/>; if a future Slang release switches to snake_case enum
/// members or changes the flag separator, the regression suite in #45 catches the drift before
/// the slangc bump merges.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(ShaderProgramDesc))]
[JsonSerializable(typeof(ShaderModuleDesc))]
[JsonSerializable(typeof(BindGroupLayoutEntryDesc))]
[JsonSerializable(typeof(BindGroupLayoutDesc))]
[JsonSerializable(typeof(PushConstantRangeDesc))]
[JsonSerializable(typeof(PipelineLayoutDesc))]
[JsonSerializable(typeof(VertexAttributeDesc))]
[JsonSerializable(typeof(VertexBufferLayoutDesc))]
public sealed partial class ShaderReflectionJsonContext : JsonSerializerContext
{
}
