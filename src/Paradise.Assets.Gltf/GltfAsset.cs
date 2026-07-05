using System.Numerics;

namespace Paradise.Assets.Gltf;

/// <summary>A fully decoded GLB: mesh instances with baked world transforms, interleaved
/// per-primitive geometry, contract-shaped materials, and raw embedded images. Pure CPU data —
/// upload/transcode decisions belong to the consumer.</summary>
public sealed record GltfAsset(
    GltfMeshInstance[] Instances,
    GltfMeshData[] Meshes,
    GltfMaterialData[] Materials,
    GltfImageData[] Images);

/// <summary>One node of the default scene that carries a mesh, with its node hierarchy baked
/// into a single world transform (System.Numerics row-vector convention; no handedness
/// conversion anywhere — the contract is RH glTF-native).</summary>
public sealed record GltfMeshInstance(
    int MeshIndex,
    Matrix4x4 WorldTransform,
    string? NodeName);

public sealed record GltfMeshData(
    string? Name,
    GltfPrimitive[] Primitives);

/// <summary>One draw batch: interleaved vertices — <see cref="FloatsPerVertex"/> floats each
/// (pos3, normal3, uv2, tangent4) — and uint indices. Attribute gaps are default-filled and
/// flagged: tangents (1,0,0,1), texcoords zero, normals +Y.</summary>
public sealed record GltfPrimitive(
    float[] Vertices,
    uint[] Indices,
    int MaterialIndex,
    bool HasNormals,
    bool HasTexCoords,
    bool HasTangents)
{
    public const int FloatsPerVertex = 12;

    public int VertexCount => Vertices.Length / FloatsPerVertex;
}

public enum GltfAlphaMode : byte
{
    Opaque = 0,
    Mask,
    Blend,
}

/// <summary>Rotation/offset/scale applied to the baseColor UV set (KHR_texture_transform).</summary>
public readonly record struct GltfUvTransform(Vector2 Offset, Vector2 Scale, float Rotation)
{
    public static readonly GltfUvTransform Identity = new(Vector2.Zero, Vector2.One, 0f);
}

/// <summary>glTF metallic-roughness material mapped onto the export contract's shape. Image
/// fields index into <see cref="GltfAsset.Images"/>; −1 = not present. KHR_texture_basisu
/// indirection is already resolved.</summary>
public sealed record GltfMaterialData(
    string? Name,
    Vector4 BaseColorFactor,
    float MetallicFactor,
    float RoughnessFactor,
    Vector3 EmissiveFactor,
    float NormalScale,
    float OcclusionStrength,
    float TransmissionFactor,
    GltfAlphaMode AlphaMode,
    float AlphaCutoff,
    bool DoubleSided,
    int BaseColorImage,
    int MetallicRoughnessImage,
    int NormalImage,
    int OcclusionImage,
    int EmissiveImage,
    GltfUvTransform BaseColorUvTransform);

public enum GltfImageKind : byte
{
    Unknown = 0,
    Png,
    Jpeg,
    Ktx2,
}

/// <summary>One embedded image: raw container bytes (PNG/JPEG/KTX2) sniffed by magic. Decode
/// and GPU-format decisions belong to the texture asset layer.</summary>
public sealed record GltfImageData(
    byte[] Bytes,
    GltfImageKind Kind);
