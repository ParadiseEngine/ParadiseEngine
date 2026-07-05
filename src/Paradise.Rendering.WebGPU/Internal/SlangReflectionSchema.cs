using System.Text.Json.Serialization;

namespace Paradise.Rendering.WebGPU.Internal;

// Records mirroring the subset of `slangc -reflection-json` output the loader consumes:
// (a) entry points, split per [shader("...")] attribute, with varying inputs driving the vertex
//     buffer layout;
// (b) top-level global parameters ("parameters"), driving bind-group layouts and uniform-block
//     byte layouts — constant buffers ("constantBuffer" with per-field {kind:"uniform", offset,
//     size} bindings and total size at elementVarLayout.binding.size), textures ("resource" with
//     baseShape "texture2D"), and samplers ("samplerState"). Binding slots come from
//     {kind:"descriptorTableSlot", space, index}; `space` is OMITTED for group 0.
// The shape is pinned by the bindings.slang golden test in Paradise.Rendering.WebGPU.Test —
// schema drift breaks that test, and only this file plus ShaderProgramLoader absorb the change.
//
// This is the *raw Slang JSON* schema, not the engine-canonical ShaderProgramDesc shape from
// Paradise.Rendering. The loader transforms one to the other so the engine surface stays stable
// even if Slang's reflection schema evolves.

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
    [property: JsonPropertyName("semanticName")] string? SemanticName);

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
    [property: JsonPropertyName("elementVarLayout")] SlangVarLayout? ElementVarLayout = null);

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
