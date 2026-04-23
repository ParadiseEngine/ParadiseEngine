using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paradise.Rendering;

/// <summary>
/// AOT/trim-safe <see cref="JsonSerializerContext"/> for the Slang-reflection-shaped records in
/// <see cref="ShaderProgramDesc"/> and friends. Snake-case-lower property naming matches Slang's
/// <c>-reflection-json</c> output schema.
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
