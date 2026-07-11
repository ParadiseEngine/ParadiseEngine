using System;
using System.Numerics;

namespace Paradise.Assets.Gltf;

/// <summary>A fully decoded GLB: mesh instances with baked world transforms, interleaved
/// per-primitive geometry, contract-shaped materials, and raw embedded images. Pure CPU data —
/// upload/transcode decisions belong to the consumer.</summary>
public sealed record GltfAsset(
    GltfMeshInstance[] Instances,
    GltfMeshData[] Meshes,
    GltfMaterialData[] Materials,
    GltfImageData[] Images,
    GltfNodeData[] Nodes,
    GltfSkinData[] Skins,
    GltfAnimationData[] Animations);

/// <summary>One node of the default scene that carries a mesh, with its node hierarchy baked
/// into a single world transform (System.Numerics row-vector convention; no handedness
/// conversion anywhere — the contract is RH glTF-native). <see cref="NodeIndex"/> addresses
/// <see cref="GltfAsset.Nodes"/>; <see cref="SkinIndex"/> is −1 for rigid meshes.</summary>
public sealed record GltfMeshInstance(
    int MeshIndex,
    Matrix4x4 WorldTransform,
    string? NodeName,
    int NodeIndex = -1,
    int SkinIndex = -1);

/// <summary>The node hierarchy with REST-pose local transforms — the animation player samples
/// channel curves over these (unanimated paths keep the rest value, glTF semantics).</summary>
public sealed record GltfNodeData(
    string? Name,
    int ParentIndex, // −1 = scene root
    Vector3 RestTranslation,
    Quaternion RestRotation,
    Vector3 RestScale);

/// <summary>One skin: joint node indices + inverse bind matrices (row-vector convention, the
/// same transpose duality as node matrices). Joint palette for a mesh instance:
/// inverseBind[i] × jointWorld[i] × inverse(meshWorld) — bank-heist's formula.</summary>
public sealed record GltfSkinData(
    string? Name,
    int[] JointNodes,
    Matrix4x4[] InverseBindMatrices);

public enum GltfAnimationPath : byte
{
    Translation = 0,
    Rotation,
    Scale,
}

/// <summary>One animation clip: per-channel keyframes targeting node T/R/S. Times are seconds
/// ascending; Values are packed floats (3 per key for T/S, 4 per key — XYZW quaternion — for
/// R). LINEAR (Lerp/Slerp) and STEP interpolation only; CUBICSPLINE is rejected at load.</summary>
public sealed record GltfAnimationData(
    string? Name,
    GltfAnimationChannelData[] Channels)
{
    public float Duration
    {
        get
        {
            var end = 0f;
            foreach (var channel in Channels)
            {
                if (channel.Times.Length > 0) end = Math.Max(end, channel.Times[^1]);
            }
            return end;
        }
    }
}

public sealed record GltfAnimationChannelData(
    int NodeIndex,
    GltfAnimationPath Path,
    bool Step, // STEP interpolation (else LINEAR)
    float[] Times,
    float[] Values);

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
    bool HasTangents,
    float[]? JointsWeights = null)
{
    public const int FloatsPerVertex = 12;

    /// <summary>Floats per vertex in <see cref="JointsWeights"/>: 4 joint indices (as floats)
    /// followed by 4 weights. Null when the primitive has no JOINTS_0/WEIGHTS_0 — the base
    /// <see cref="Vertices"/> stream is unchanged either way (CPU skinning reads both).</summary>
    public const int SkinFloatsPerVertex = 8;

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

/// <summary>One embedded image. ALWAYS a KTX2 container — the contract mandates KTX2 for every
/// texture (the toktx pass in the export pipeline), and the reader rejects anything else at
/// load time. Transcode decisions (BC vs RGBA32) belong to the texture asset layer.</summary>
public sealed record GltfImageData(
    byte[] Bytes);
