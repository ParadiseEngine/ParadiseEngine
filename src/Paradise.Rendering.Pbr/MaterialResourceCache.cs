using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Assets.Gltf;
using Paradise.Assets.Textures;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr;

/// <summary>GPU-side material store (the port of bank-heist's TextureMaterialResourceCache):
/// per-material 80-byte UBO + group-2 bind group (UBO, five textures, one shared sampler),
/// 1×1 defaults for absent maps, KTX2 transcode → BC (or RGBA32 when the adapter lacks BC),
/// and image dedupe keyed by (content hash, usage) — the same KTX2 payload used as color vs
/// data transcodes to different formats, so usage is part of texture identity.</summary>
public sealed class MaterialResourceCache : IDisposable
{
    private readonly WebGpuRenderer _renderer;
    private readonly BindGroupLayoutDesc _materialGroupLayout;
    private readonly SamplerHandle _sampler;
    private readonly TextureHandle _defaultWhite;
    private readonly TextureHandle _defaultNormal;
    // Keyed by image CONTENT (SHA-256), not image index: indices are per-GLB, so two assets
    // both referencing "image 0" would otherwise collide on one texture. Content keying also
    // dedupes byte-identical images across assets.
    private readonly Dictionary<(string ContentHash, CompressedTextureUsage Usage), TextureHandle> _textureCache = new();
    private readonly List<(BufferHandle Ubo, BindGroupHandle Group, bool Blend)> _materials = [];
    private readonly List<TextureHandle> _ownedTextures = [];
    private bool _disposed;

    /// <summary>Distinct GPU textures uploaded (excludes the two defaults) — dedupe metric.</summary>
    public int TextureCount => _textureCache.Count;

    public int MaterialCount => _materials.Count;

    public MaterialResourceCache(WebGpuRenderer renderer, ShaderProgramDesc program, ushort maxAnisotropy = 16)
    {
        _renderer = renderer;
        _materialGroupLayout = FindGroup(program, 2);

        var samplerDesc = new SamplerDesc(
            "PbrMaterialSampler",
            SamplerAddressMode.Repeat, SamplerAddressMode.Repeat, SamplerAddressMode.Repeat,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Linear,
            maxAnisotropy);
        _sampler = renderer.CreateSampler(in samplerDesc);

        // Defaults: white drives factor-only materials for every slot except normals (flat
        // tangent-space normal, X=Y=0.5 in the two-channel convention).
        _defaultWhite = CreateSolidTexture("PbrDefaultWhite", 255, 255, 255, 255);
        _defaultNormal = CreateSolidTexture("PbrDefaultNormal", 128, 128, 255, 255);
    }

    /// <summary>Create the GPU resources for one material and return its id. Textures resolve
    /// through <paramref name="images"/> (KTX2 payloads, PR #68's guarantee).</summary>
    public int AddMaterial(in GltfMaterialData material, GltfImageData[] images)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var uniforms = new MaterialUniformsGpu
        {
            BaseColorFactor = material.BaseColorFactor,
            MetallicFactor = material.MetallicFactor,
            RoughnessFactor = material.RoughnessFactor,
            NormalScale = material.NormalScale,
            OcclusionStrength = material.OcclusionStrength,
            EmissiveFactor = new Vector4(material.EmissiveFactor, material.TransmissionFactor),
            UvOffsetScale = new Vector4(
                material.BaseColorUvTransform.Offset.X, material.BaseColorUvTransform.Offset.Y,
                material.BaseColorUvTransform.Scale.X, material.BaseColorUvTransform.Scale.Y),
            UvRotation = new Vector4(material.BaseColorUvTransform.Rotation, 0f, 0f, 0f),
            ProcColorA = new Vector4(material.ProcColorA, 0f),
            ProcColorB = new Vector4(material.ProcColorB, 0f),
            ProcParams = new Vector4(material.ProcKind, material.ProcNoiseScale, material.ProcFlowSpeed, material.ProcEmissiveStrength),
        };

        var uboDesc = new BufferDesc($"PbrMaterial[{_materials.Count}]", 0, BufferUsage.Uniform);
        var ubo = _renderer.CreateBufferWithData(in uboDesc, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        var baseColor = ResolveTexture(material.BaseColorImage, images, CompressedTextureUsage.ColorSrgb, _defaultWhite);
        var metallicRoughness = ResolveTexture(material.MetallicRoughnessImage, images, CompressedTextureUsage.LinearData, _defaultWhite);
        var normal = ResolveTexture(material.NormalImage, images, CompressedTextureUsage.NormalMap, _defaultNormal);
        var occlusion = ResolveTexture(material.OcclusionImage, images, CompressedTextureUsage.LinearData, _defaultWhite);
        var emissive = ResolveTexture(material.EmissiveImage, images, CompressedTextureUsage.ColorSrgb, _defaultWhite);

        var groupDesc = new BindGroupDesc($"PbrMaterialGroup[{_materials.Count}]", _materialGroupLayout, new[]
        {
            BindGroupEntryDesc.ForBuffer(0, ubo, 0, (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<MaterialUniformsGpu>()),
            BindGroupEntryDesc.ForTexture(1, baseColor),
            BindGroupEntryDesc.ForSampler(2, _sampler),
            BindGroupEntryDesc.ForTexture(3, metallicRoughness),
            BindGroupEntryDesc.ForTexture(4, normal),
            BindGroupEntryDesc.ForTexture(5, occlusion),
            BindGroupEntryDesc.ForTexture(6, emissive),
        });
        var group = _renderer.CreateBindGroup(in groupDesc);

        // Transmission needs the alpha-blend pipeline even for AlphaMode=Opaque materials.
        var blend = material.AlphaMode == GltfAlphaMode.Blend || material.TransmissionFactor > 0f;
        _materials.Add((ubo, group, blend));
        return _materials.Count - 1;
    }

    /// <summary>A factor-only default material (used by procedural meshes and null slots).</summary>
    public int AddDefaultMaterial(Vector4 baseColorFactor, float metallic = 0f, float roughness = 0.8f)
    {
        var material = new GltfMaterialData(
            Name: "default",
            BaseColorFactor: baseColorFactor,
            MetallicFactor: metallic,
            RoughnessFactor: roughness,
            EmissiveFactor: Vector3.Zero,
            NormalScale: 1f,
            OcclusionStrength: 1f,
            TransmissionFactor: 0f,
            AlphaMode: GltfAlphaMode.Opaque,
            AlphaCutoff: 0.5f,
            DoubleSided: false,
            BaseColorImage: -1,
            MetallicRoughnessImage: -1,
            NormalImage: -1,
            OcclusionImage: -1,
            EmissiveImage: -1,
            BaseColorUvTransform: GltfUvTransform.Identity);
        return AddMaterial(in material, []);
    }

    public BindGroupHandle GetBindGroup(int materialId) => _materials[materialId].Group;

    public bool IsBlend(int materialId) => _materials[materialId].Blend;

    private TextureHandle ResolveTexture(
        int imageIndex, GltfImageData[] images, CompressedTextureUsage usage, TextureHandle fallback)
    {
        if (imageIndex < 0) return fallback;
        if ((uint)imageIndex >= (uint)images.Length)
            throw new ArgumentException($"Material references image {imageIndex} but the asset has {images.Length}.");

        // Hashing the (already-small, supercompressed) KTX2 bytes is trivial next to a
        // transcode and buys cross-asset correctness — see the _textureCache comment.
        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(images[imageIndex].Bytes));
        if (_textureCache.TryGetValue((contentHash, usage), out var cached)) return cached;

        var transcoded = _renderer.SupportsBcTextureCompression
            ? Ktx2Transcoder.TranscodeToBc(images[imageIndex].Bytes, usage)
            : Ktx2Transcoder.TranscodeToRgba32(images[imageIndex].Bytes, usage);
        if (transcoded.IsEmpty)
        {
            // Malformed payload → the transcoder's empty sentinel → visible-but-wrong default,
            // matching the transcoder contract (no throw at render-load time).
            _textureCache[(contentHash, usage)] = fallback;
            return fallback;
        }

        var desc = new TextureDesc(
            $"PbrTexture[{contentHash[..8]},{usage}]",
            (uint)transcoded.Width, (uint)transcoded.Height, 1,
            (uint)transcoded.MipLevels.Length, 1,
            TextureDimension.D2,
            transcoded.Format,
            TextureUsage.TextureBinding | TextureUsage.CopyDst);
        var handle = _renderer.CreateTexture(in desc);
        for (var level = 0; level < transcoded.MipLevels.Length; level++)
        {
            var mip = transcoded.MipLevels[level];
            _renderer.WriteTexture(
                handle, (uint)level,
                transcoded.Data.AsSpan(mip.Offset, mip.Length),
                (uint)mip.BytesPerRow, (uint)mip.Rows,
                (uint)mip.Width, (uint)mip.Height);
        }

        _textureCache[(contentHash, usage)] = handle;
        _ownedTextures.Add(handle);
        return handle;
    }

    private TextureHandle CreateSolidTexture(string name, byte r, byte g, byte b, byte a)
    {
        var desc = new TextureDesc(
            name, 1, 1, 1, 1, 1, TextureDimension.D2,
            TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst);
        var handle = _renderer.CreateTexture(in desc);
        _renderer.WriteTexture(handle, 0, [r, g, b, a], 4, 1, 1, 1);
        return handle;
    }

    private static BindGroupLayoutDesc FindGroup(ShaderProgramDesc program, uint groupIndex)
    {
        foreach (var group in program.Layout.Groups)
        {
            if (group.GroupIndex == groupIndex) return group;
        }
        throw new InvalidOperationException($"PBR program reflects no bind group {groupIndex}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (ubo, group, _) in _materials)
        {
            _renderer.DestroyBindGroup(group);
            _renderer.DestroyBuffer(ubo);
        }
        foreach (var texture in _ownedTextures) _renderer.DestroyTexture(texture);
        _renderer.DestroyTexture(_defaultNormal);
        _renderer.DestroyTexture(_defaultWhite);
        _renderer.DestroySampler(_sampler);
    }
}
