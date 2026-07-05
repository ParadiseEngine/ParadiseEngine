using System.Text.Json.Serialization;

namespace Paradise.Assets.Gltf.Json;

// Source-generated records mirroring the glTF 2.0 JSON subset the Paradise export contract
// produces: static meshes (POSITION/NORMAL/TANGENT/TEXCOORD_0 + indices), metallic-roughness
// materials (+ KHR_materials_transmission), embedded images, KHR_texture_basisu,
// KHR_texture_transform (baseColor). Records stay tolerant of extra keys (default STJ behavior)
// so richer producers still load; unsupported *structural* features (sparse accessors,
// non-triangle modes, external buffers) are rejected by GltfSceneReader with clear messages.

internal sealed record GltfRoot(
    [property: JsonPropertyName("asset")] GltfAssetInfo? Asset,
    [property: JsonPropertyName("scene")] int? Scene,
    [property: JsonPropertyName("scenes")] GltfScene[]? Scenes,
    [property: JsonPropertyName("nodes")] GltfNode[]? Nodes,
    [property: JsonPropertyName("meshes")] GltfMesh[]? Meshes,
    [property: JsonPropertyName("accessors")] GltfAccessor[]? Accessors,
    [property: JsonPropertyName("bufferViews")] GltfBufferView[]? BufferViews,
    [property: JsonPropertyName("buffers")] GltfBuffer[]? Buffers,
    [property: JsonPropertyName("materials")] GltfMaterial[]? Materials,
    [property: JsonPropertyName("textures")] GltfTexture[]? Textures,
    [property: JsonPropertyName("images")] GltfImage[]? Images);

internal sealed record GltfAssetInfo(
    [property: JsonPropertyName("version")] string? Version);

internal sealed record GltfScene(
    [property: JsonPropertyName("nodes")] int[]? Nodes);

internal sealed record GltfNode(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("children")] int[]? Children,
    [property: JsonPropertyName("mesh")] int? Mesh,
    [property: JsonPropertyName("matrix")] float[]? Matrix,
    [property: JsonPropertyName("translation")] float[]? Translation,
    [property: JsonPropertyName("rotation")] float[]? Rotation,
    [property: JsonPropertyName("scale")] float[]? Scale);

internal sealed record GltfMesh(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("primitives")] GltfPrimitiveJson[]? Primitives);

internal sealed record GltfPrimitiveJson(
    [property: JsonPropertyName("attributes")] Dictionary<string, int>? Attributes,
    [property: JsonPropertyName("indices")] int? Indices,
    [property: JsonPropertyName("material")] int? Material,
    [property: JsonPropertyName("mode")] int? Mode);

internal sealed record GltfAccessor(
    [property: JsonPropertyName("bufferView")] int? BufferView,
    [property: JsonPropertyName("byteOffset")] int? ByteOffset,
    [property: JsonPropertyName("componentType")] int ComponentType,
    [property: JsonPropertyName("normalized")] bool Normalized,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("sparse")] GltfSparse? Sparse);

/// <summary>Presence marker only — sparse accessors are rejected, so the payload is unmodeled.</summary>
internal sealed record GltfSparse(
    [property: JsonPropertyName("count")] int Count);

internal sealed record GltfBufferView(
    [property: JsonPropertyName("buffer")] int Buffer,
    [property: JsonPropertyName("byteOffset")] int? ByteOffset,
    [property: JsonPropertyName("byteLength")] int ByteLength,
    [property: JsonPropertyName("byteStride")] int? ByteStride);

internal sealed record GltfBuffer(
    [property: JsonPropertyName("byteLength")] int ByteLength,
    [property: JsonPropertyName("uri")] string? Uri);

internal sealed record GltfMaterial(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("pbrMetallicRoughness")] GltfPbrMetallicRoughness? PbrMetallicRoughness,
    [property: JsonPropertyName("normalTexture")] GltfNormalTextureRef? NormalTexture,
    [property: JsonPropertyName("occlusionTexture")] GltfOcclusionTextureRef? OcclusionTexture,
    [property: JsonPropertyName("emissiveTexture")] GltfTextureRef? EmissiveTexture,
    [property: JsonPropertyName("emissiveFactor")] float[]? EmissiveFactor,
    [property: JsonPropertyName("alphaMode")] string? AlphaMode,
    [property: JsonPropertyName("alphaCutoff")] float? AlphaCutoff,
    [property: JsonPropertyName("doubleSided")] bool DoubleSided,
    [property: JsonPropertyName("extensions")] GltfMaterialExtensions? Extensions);

internal sealed record GltfMaterialExtensions(
    [property: JsonPropertyName("KHR_materials_transmission")] GltfTransmission? Transmission);

internal sealed record GltfTransmission(
    [property: JsonPropertyName("transmissionFactor")] float TransmissionFactor);

internal sealed record GltfPbrMetallicRoughness(
    [property: JsonPropertyName("baseColorFactor")] float[]? BaseColorFactor,
    [property: JsonPropertyName("baseColorTexture")] GltfTextureRef? BaseColorTexture,
    [property: JsonPropertyName("metallicFactor")] float? MetallicFactor,
    [property: JsonPropertyName("roughnessFactor")] float? RoughnessFactor,
    [property: JsonPropertyName("metallicRoughnessTexture")] GltfTextureRef? MetallicRoughnessTexture);

internal sealed record GltfTextureRef(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("texCoord")] int? TexCoord,
    [property: JsonPropertyName("extensions")] GltfTextureRefExtensions? Extensions);

internal sealed record GltfTextureRefExtensions(
    [property: JsonPropertyName("KHR_texture_transform")] GltfTextureTransform? Transform);

internal sealed record GltfTextureTransform(
    [property: JsonPropertyName("offset")] float[]? Offset,
    [property: JsonPropertyName("rotation")] float? Rotation,
    [property: JsonPropertyName("scale")] float[]? Scale);

internal sealed record GltfNormalTextureRef(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("scale")] float? Scale);

internal sealed record GltfOcclusionTextureRef(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("strength")] float? Strength);

internal sealed record GltfTexture(
    [property: JsonPropertyName("source")] int? Source,
    [property: JsonPropertyName("sampler")] int? Sampler,
    [property: JsonPropertyName("extensions")] GltfTextureExtensions? Extensions);

internal sealed record GltfTextureExtensions(
    [property: JsonPropertyName("KHR_texture_basisu")] GltfBasisu? Basisu);

internal sealed record GltfBasisu(
    [property: JsonPropertyName("source")] int Source);

internal sealed record GltfImage(
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("mimeType")] string? MimeType,
    [property: JsonPropertyName("bufferView")] int? BufferView);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GltfRoot))]
internal sealed partial class GltfJsonContext : JsonSerializerContext
{
}
