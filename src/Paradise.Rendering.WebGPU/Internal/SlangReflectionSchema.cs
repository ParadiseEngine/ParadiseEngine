using System.Text.Json.Serialization;

namespace Paradise.Rendering.WebGPU.Internal;

// Records mirroring the subset of `slangc -reflection-json` output the M1 path consumes. Only the
// fields needed to (a) split modules per [shader("...")] entry point, and (b) derive the vertex
// buffer layout from the vs entry point's struct-typed input parameter are modeled. Other fields
// (bindings, push constants, occluded output bindings, etc.) are ignored at deserialization but
// the records stay tolerant of extra keys via the JsonSerializer's default behavior.
//
// This shape is the *raw Slang JSON* schema, not the engine-canonical ShaderProgramDesc shape from
// Paradise.Rendering. The loader transforms one to the other so the engine surface stays stable
// even if Slang's reflection schema evolves.

internal sealed record SlangReflection(
    [property: JsonPropertyName("entryPoints")] SlangEntryPoint[]? EntryPoints);

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
    [property: JsonPropertyName("count")] uint? Count);

internal sealed record SlangTypeNode(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("fields")] SlangField[]? Fields,
    [property: JsonPropertyName("elementCount")] uint? ElementCount,
    [property: JsonPropertyName("elementType")] SlangTypeNode? ElementType,
    [property: JsonPropertyName("scalarType")] string? ScalarType);

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
