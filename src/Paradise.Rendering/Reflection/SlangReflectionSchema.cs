using System.Text.Json.Serialization;

namespace Paradise.Rendering;

// Internal Slang reflection schema: entry points supply vertex inputs; global parameters supply
// bindings and uniform offsets. Group-zero space may be omitted. ShaderProgramLoader converts these
// records to the public engine shape; bindings.slang tests detect schema drift.

internal sealed record SlangReflection(
    [property: JsonPropertyName("entryPoints")] SlangEntryPoint[]? EntryPoints,
    [property: JsonPropertyName("parameters")] SlangParameter[]? Parameters = null);

internal sealed record SlangEntryPoint(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("parameters")] SlangParameter[]? Parameters);

internal sealed record SlangParameter(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("binding")] SlangBinding? Binding,
    [property: JsonPropertyName("type")] SlangTypeNode? Type,
    [property: JsonPropertyName("semanticName")] string? SemanticName,
    // The [format("...")] attribute of a storage texture, in GLSL image-format spelling
    // ("rgba16f") — reflected at the PARAMETER level, not on the type node. Absent when the
    // attribute is missing (in which case slangc silently defaults the WGSL to rgba32float —
    // the loader throws instead of guessing).
    [property: JsonPropertyName("format")] string? Format = null);

internal sealed record SlangBinding(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("index")] uint Index,
    [property: JsonPropertyName("count")] uint? Count,
    [property: JsonPropertyName("space")] uint? Space = null,
    [property: JsonPropertyName("offset")] uint? Offset = null,
    [property: JsonPropertyName("size")] uint? Size = null,
    [property: JsonPropertyName("elementStride")] uint? ElementStride = null);

internal sealed record SlangTypeNode(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("fields")] SlangField[]? Fields,
    [property: JsonPropertyName("elementCount")] uint? ElementCount,
    [property: JsonPropertyName("elementType")] SlangTypeNode? ElementType,
    [property: JsonPropertyName("scalarType")] string? ScalarType,
    [property: JsonPropertyName("baseShape")] string? BaseShape = null,
    [property: JsonPropertyName("uniformStride")] uint? UniformStride = null,
    [property: JsonPropertyName("elementVarLayout")] SlangVarLayout? ElementVarLayout = null,
    // RW resource access: "write" (WTexture2D), "readWrite" (RWTexture2D / RWStructuredBuffer),
    // "read", or absent for ordinary read-only resources.
    [property: JsonPropertyName("access")] string? Access = null,
    [property: JsonPropertyName("array")] bool Array = false);

internal sealed record SlangVarLayout(
    [property: JsonPropertyName("type")] SlangTypeNode? Type,
    [property: JsonPropertyName("binding")] SlangBinding? Binding);

internal sealed record SlangField(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] SlangTypeNode Type,
    [property: JsonPropertyName("semanticName")] string? SemanticName,
    [property: JsonPropertyName("binding")] SlangBinding? Binding);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SlangReflection))]
internal sealed partial class SlangReflectionJsonContext : JsonSerializerContext
{
}
