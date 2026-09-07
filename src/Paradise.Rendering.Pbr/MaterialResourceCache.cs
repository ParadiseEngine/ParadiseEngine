using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Assets.Gltf;
using Paradise.Assets.Textures;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>An extra material binding that follows a frame target by name — the scene color a
/// refracting material samples, say — instead of holding a view handle that a resize retires.
/// The cache re-resolves it every frame and rebuilds the group only when the view changed;
/// while the target does not exist the binding is black, never dangling.</summary>
public readonly record struct MaterialTarget(uint Binding, string Target);

/// <summary>GPU-side material store (the port of bank-heist's TextureMaterialResourceCache):
/// per-material 80-byte UBO + group-2 bind group (UBO, five textures, one shared sampler),
/// 1×1 defaults for absent maps, KTX2 transcode → BC (or RGBA32 when the adapter lacks BC),
/// and image dedupe keyed by (content hash, usage) — the same KTX2 payload used as color vs
/// data transcodes to different formats, so usage is part of texture identity.</summary>
/// <summary>A material as a ray hit sees it: albedo, metallic and emissive factors, no textures.</summary>
public readonly record struct TraceSurface(Vector4 BaseColor, Vector3 Emissive, float Metallic);

public sealed class MaterialResourceCache : IDisposable
{
    private readonly IRenderer _renderer;
    private readonly BindGroupLayoutDesc _materialGroupLayout;
    private readonly SamplerHandle _sampler;
    private readonly TextureHandle _defaultWhite;
    private readonly TextureHandle _defaultNormal;
    // Keyed by image CONTENT (SHA-256), not image index: indices are per-GLB, so two assets
    // both referencing "image 0" would otherwise collide on one texture. Content keying also
    // dedupes byte-identical images across assets.
    private readonly Dictionary<(string ContentHash, CompressedTextureUsage Usage), TextureHandle> _textureCache = new();
    private readonly List<(BufferHandle Ubo, BindGroupHandle Group, bool Blend, int ProgramId, BindGroupEntryDesc[] Entries, BindGroupLayoutDesc Layout)> _materials = [];
    // What a ray hit reads of a material: the factors alone, since a hit samples no textures.
    private readonly List<TraceSurface> _surfaces = [];
    // Materials with target-following entries, and the view each entry was last built with.
    private readonly Dictionary<int, TargetSet> _targets = new();
    private readonly GraphTextureRegistry? _registry;
    private readonly List<TextureHandle> _ownedTextures = [];
    // Group-2 layouts of registered custom programs (PbrRenderer.RegisterMaterialProgram): the
    // standard seven entries plus that program's extras, in binding order.
    private readonly Dictionary<int, BindGroupLayoutDesc> _programGroup2Layouts = new();
    private bool _disposed;

    /// <summary>The built-in group-2 entries every material carries: the material UBO, five
    /// textures and the shared sampler (bindings 0..6). Custom programs add theirs from 7 up.</summary>
    public const int StandardMaterialEntryCount = 7;

    /// <summary>Distinct GPU textures uploaded (excludes the two defaults) — dedupe metric.</summary>
    public int TextureCount => _textureCache.Count;

    public int MaterialCount => _materials.Count;

    public MaterialResourceCache(IRenderer renderer, ShaderProgramDesc program, ushort maxAnisotropy = 16)
        : this(renderer, program, maxAnisotropy, registry: null)
    {
    }

    /// <param name="registry">Where <see cref="MaterialTarget"/> bindings resolve. Null forbids them.</param>
    internal MaterialResourceCache(IRenderer renderer, ShaderProgramDesc program, ushort maxAnisotropy, GraphTextureRegistry? registry)
    {
        _renderer = renderer;
        _registry = registry;
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
        => AddMaterial(in material, images, programId: 0);

    /// <summary>Create a material bound to a shader program registered via
    /// <c>PbrRenderer.RegisterMaterialProgram</c> (programId 0 = the built-in PBR program). The
    /// group-2 bind group is built from THAT program's layout: the standard seven entries first,
    /// then <paramref name="extraEntries"/> in binding order (e.g.
    /// <c>BindGroupEntryDesc.ForTextureView(7, heightfieldView)</c>). Extra-bound resources are
    /// OWNED BY THE CALLER (never disposed here), and per-frame <c>IRenderer.WriteTexture</c> into
    /// them is the caller's channel for dynamic shader data — the material UBO itself stays
    /// immutable after creation.</summary>
    public int AddMaterial(in GltfMaterialData material, GltfImageData[] images,
        int programId, ReadOnlySpan<BindGroupEntryDesc> extraEntries = default)
        => AddMaterial(in material, images, programId, extraEntries, targets: default);

    /// <summary>As above, with some extra bindings following frame targets by name. A
    /// <see cref="MaterialTarget"/> for <see cref="PbrTargets.SceneColor"/> replaces holding
    /// <c>PbrRenderer.SceneColorView</c> and rebinding on <c>SceneColorViewChanged</c>: the cache
    /// re-resolves it each frame, and it reads black while capture is off.</summary>
    public int AddMaterial(in GltfMaterialData material, GltfImageData[] images,
        int programId, ReadOnlySpan<BindGroupEntryDesc> extraEntries, ReadOnlySpan<MaterialTarget> targets)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (targets.Length > 0 && _registry is null)
            throw new InvalidOperationException("This material cache has no target registry; only a PbrRenderer's can bind targets.");

        var layout = _materialGroupLayout;
        if (programId != 0)
        {
            if (!_programGroup2Layouts.TryGetValue(programId, out layout!))
                throw new ArgumentException(
                    $"Unknown material program {programId}; register it via PbrRenderer.RegisterMaterialProgram first.",
                    nameof(programId));
        }
        var extraCount = extraEntries.Length + targets.Length;
        if (layout.Entries.Length != StandardMaterialEntryCount + extraCount)
            throw new ArgumentException(
                $"Material program {programId} declares {layout.Entries.Length - StandardMaterialEntryCount} extra " +
                $"group-2 binding(s), but {extraCount} extra entr{(extraCount == 1 ? "y was" : "ies were")} supplied.",
                nameof(extraEntries));

        // Extras in the layout's binding order, each taken from whichever list names its binding.
        var extras = new BindGroupEntryDesc[extraCount];
        var bound = new (MaterialTarget Target, TextureViewHandle Bound)[targets.Length];
        for (var i = 0; i < extraCount; i++)
        {
            var expected = layout.Entries[StandardMaterialEntryCount + i];
            var found = false;
            foreach (var entry in extraEntries)
            {
                if (entry.Binding != expected.Binding) continue;
                // Kind-vs-type check here turns what would be a native CreateBindGroup validation
                // error (e.g. a sampler supplied where the shader declares a texture) into the
                // same clear ArgumentException the slot checks raise.
                if (!EntryKindMatches(entry.Kind, expected.Type))
                    throw new ArgumentException(
                        $"Extra entry at binding {expected.Binding} supplies a {entry.Kind}, " +
                        $"but material program {programId} declares a {expected.Type} there.",
                        nameof(extraEntries));
                extras[i] = entry;
                found = true;
                break;
            }
            for (var t = 0; t < targets.Length && !found; t++)
            {
                if (targets[t].Binding != expected.Binding) continue;
                if (!EntryKindMatches(BindGroupEntryKind.TextureView, expected.Type))
                    throw new ArgumentException(
                        $"Target '{targets[t].Target}' binds slot {expected.Binding}, but material program {programId} " +
                        $"declares a {expected.Type} there, not a texture.", nameof(targets));
                var view = ResolveTarget(targets[t].Target);
                extras[i] = BindGroupEntryDesc.ForTextureView(expected.Binding, view);
                bound[t] = (targets[t], view);
                found = true;
            }
            if (!found)
                throw new ArgumentException(
                    $"Material program {programId} declares binding {expected.Binding} in group 2, but no extra entry " +
                    "or target supplies it.", nameof(extraEntries));
        }

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

        var entries = new BindGroupEntryDesc[StandardMaterialEntryCount + extraCount];
        entries[0] = BindGroupEntryDesc.ForBuffer(0, ubo, 0, (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<MaterialUniformsGpu>());
        entries[1] = BindGroupEntryDesc.ForTexture(1, baseColor);
        entries[2] = BindGroupEntryDesc.ForSampler(2, _sampler);
        entries[3] = BindGroupEntryDesc.ForTexture(3, metallicRoughness);
        entries[4] = BindGroupEntryDesc.ForTexture(4, normal);
        entries[5] = BindGroupEntryDesc.ForTexture(5, occlusion);
        entries[6] = BindGroupEntryDesc.ForTexture(6, emissive);
        extras.CopyTo(entries, StandardMaterialEntryCount);
        var groupDesc = new BindGroupDesc($"PbrMaterialGroup[{_materials.Count}]", layout, entries);
        var group = _renderer.CreateBindGroup(in groupDesc);

        // Transmission needs the alpha-blend pipeline even for AlphaMode=Opaque materials.
        var blend = material.AlphaMode == GltfAlphaMode.Blend || material.TransmissionFactor > 0f;
        // Entries + layout are retained so a group can be rebuilt with one entry changed.
        _materials.Add((ubo, group, blend, programId, entries, layout));
        _surfaces.Add(new TraceSurface(material.BaseColorFactor, material.EmissiveFactor, material.MetallicFactor));
        var materialId = _materials.Count - 1;
        if (bound.Length > 0) _targets[materialId] = new TargetSet(bound);
        return materialId;
    }

    /// <summary>The frame targets <paramref name="materialId"/> follows, by name. What a pass
    /// drawing the material reads.</summary>
    public ReadOnlySpan<string> TargetsOf(int materialId) =>
        _targets.TryGetValue(materialId, out var set) ? set.Names : default;

    private sealed class TargetSet((MaterialTarget Target, TextureViewHandle Bound)[] bound)
    {
        public readonly (MaterialTarget Target, TextureViewHandle Bound)[] Bound = bound;
        public readonly string[] Names = Array.ConvertAll(bound, static b => b.Target.Target);
    }

    /// <summary>Re-resolve every target-following entry and rebuild the groups whose view changed:
    /// a resize recreated the target, or capture was switched on or off. Once per frame, before
    /// recording.</summary>
    internal void ResolveTargets()
    {
        foreach (var (materialId, set) in _targets)
        {
            var bound = set.Bound;
            var changed = false;
            for (var t = 0; t < bound.Length; t++)
            {
                var view = ResolveTarget(bound[t].Target.Target);
                if (view == bound[t].Bound) continue;
                bound[t].Bound = view;
                changed = true;
            }
            if (!changed) continue;

            var (ubo, group, blend, programId, entries, layout) = _materials[materialId];
            for (var t = 0; t < bound.Length; t++)
                for (var i = StandardMaterialEntryCount; i < entries.Length; i++)
                    if (entries[i].Binding == bound[t].Target.Binding)
                        entries[i] = BindGroupEntryDesc.ForTextureView(entries[i].Binding, bound[t].Bound);
            _renderer.DestroyBindGroup(group);
            var rebuilt = _renderer.CreateBindGroup(new BindGroupDesc($"PbrMaterialGroup[{materialId}]", layout, entries));
            _materials[materialId] = (ubo, rebuilt, blend, programId, entries, layout);
        }
    }

    // Black while the target does not exist, so a binding never dangles and a shader that
    // samples it anyway reads zero.
    private TextureViewHandle ResolveTarget(string name) =>
        _registry!.Contains(name) ? _registry.View(name) : _registry.View(_registry.Black);

    /// <summary>Replace one EXTRA entry (binding >= <see cref="StandardMaterialEntryCount"/>) of a
    /// material and rebuild its bind group. For a caller-owned resource that changed. An
    /// engine-owned target is better bound as a <see cref="MaterialTarget"/>, which the cache
    /// keeps current itself; this remains the path for a view handle bound by hand, rebound from
    /// <c>PbrRenderer.SceneColorViewChanged</c>. The old group is destroyed synchronously (in-flight
    /// GPU work stays valid, the same contract every engine-side rebuild relies on);
    /// <see cref="GetBindGroup"/> returns the new group from the next frame.</summary>
    public void UpdateExtraEntry(int materialId, in BindGroupEntryDesc entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (ubo, group, blend, programId, entries, layout) = _materials[materialId];
        if (entry.Binding < StandardMaterialEntryCount)
            throw new ArgumentException(
                $"Binding {entry.Binding} is a standard material entry (0..{StandardMaterialEntryCount - 1}) — " +
                "only extra entries can be updated.", nameof(entry));
        var index = -1;
        for (var i = StandardMaterialEntryCount; i < entries.Length; i++)
        {
            if (entries[i].Binding == entry.Binding) { index = i; break; }
        }
        if (index < 0)
            throw new ArgumentException(
                $"Material {materialId} has no extra entry at binding {entry.Binding}.", nameof(entry));
        var expected = layout.Entries[index];
        if (!EntryKindMatches(entry.Kind, expected.Type))
            throw new ArgumentException(
                $"Extra entry at binding {entry.Binding} supplies a {entry.Kind}, but material program " +
                $"{programId} declares a {expected.Type} there.", nameof(entry));

        entries[index] = entry;
        _renderer.DestroyBindGroup(group);
        var rebuilt = _renderer.CreateBindGroup(new BindGroupDesc($"PbrMaterialGroup[{materialId}]", layout, entries));
        _materials[materialId] = (ubo, rebuilt, blend, programId, entries, layout);
    }

    /// <summary>The shader program a material draws with — 0 for the built-in PBR program.</summary>
    public int GetProgramId(int materialId) => _materials[materialId].ProgramId;

    internal void RegisterProgramLayout(int programId, in BindGroupLayoutDesc group2Layout)
        => _programGroup2Layouts[programId] = group2Layout;

    private static bool EntryKindMatches(BindGroupEntryKind kind, BindingResourceType type) => type switch
    {
        BindingResourceType.UniformBuffer or BindingResourceType.StorageBuffer
            or BindingResourceType.ReadonlyStorageBuffer => kind == BindGroupEntryKind.Buffer,
        BindingResourceType.Sampler or BindingResourceType.ComparisonSampler => kind == BindGroupEntryKind.Sampler,
        _ => kind is BindGroupEntryKind.Texture or BindGroupEntryKind.TextureView,
    };

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

    /// <summary>The factor-only surface the tracer shades a hit on this material with.</summary>
    public TraceSurface GetTraceSurface(int materialId) => _surfaces[materialId];

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
        foreach (var (ubo, group, _, _, _, _) in _materials)
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
