using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paradise.Rendering;

/// <summary>Provides AOT-safe JSON serialization for Slang reflection records.</summary>
/// <remarks>Uses snake-case property names and PascalCase enum values. Reflection and round-trip
/// tests detect drift from the pinned compiler schema.</remarks>
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
