using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Assets.Textures;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>An extra material binding that follows a frame target by name — the scene color a
/// refracting material samples, say — instead of holding a view handle that a resize retires.
/// The cache re-resolves it every frame and rebuilds the group only when the view changed;
/// while the target does not exist the binding is black, never dangling.</summary>
public readonly record struct MaterialTarget(uint Binding, string Target);

/// <summary>A material as a ray hit sees it: albedo, metallic and emissive factors, no textures.</summary>
public readonly record struct TraceSurface(Vector4 BaseColor, Vector3 Emissive, float Metallic);

/// <summary>GPU-side material store (the port of bank-heist's TextureMaterialResourceCache):
/// per-material 80-byte UBO + group-2 bind group (UBO, five textures, one shared sampler),
/// 1×1 defaults for absent maps, KTX2 transcode → BC (or RGBA32 when the adapter lacks BC),
/// and image dedupe keyed by (content hash, usage) — the same KTX2 payload used as color vs
/// data transcodes to different formats, so usage is part of texture identity.</summary>
public sealed class MaterialResourceCache : IDisposable
{
    private readonly IRenderer _renderer;
    private readonly BindGroupLayoutDesc _materialGroupLayout;
    private readonly SamplerHandle _sampler;
    private readonly TextureHandle _defaultWhite;
    private readonly TextureHandle _defaultNormal;
    // Content and usage identify a cooked texture independently of asset paths or containers.
    private readonly Dictionary<TextureKey, TextureEntry> _textureCache = new();
    // Released slots remain empty: an old primitive must never resolve to a different material.
    private readonly List<MaterialEntry?> _materials = [];
    private int _materialCount;
    // Materials with target-following entries, and the view each entry was last built with.
    private readonly Dictionary<int, TargetSet> _targets = new();
    private readonly GraphTextureRegistry? _registry;
    // Group-2 layouts of registered custom programs (PbrRenderer.RegisterMaterialProgram): the
    // standard seven entries plus that program's extras, in binding order.
    private readonly Dictionary<int, BindGroupLayoutDesc> _programGroup2Layouts = new();
    private readonly Dictionary<int, MaterialProgramOptions> _programOptions = new();
    private bool _disposed;

    private readonly record struct TextureKey(string ContentHash, CompressedTextureUsage Usage);

    private sealed class TextureEntry(TextureHandle handle, bool owned)
    {
        public readonly TextureHandle Handle = handle;
        public readonly bool Owned = owned;
        public int References = 1;
    }

    private sealed record MaterialEntry(BufferHandle Ubo, BindGroupHandle Group, bool Blend, int ProgramId,
        BindGroupEntryDesc[] Entries, BindGroupLayoutDesc Layout, TextureKey[] Textures,
        TraceSurface Surface, bool Occluder, bool Reorderable);

    /// <summary>The built-in group-2 entries every material carries: the material UBO, five
    /// textures and the shared sampler (bindings 0..6). Custom programs add theirs from 7 up.</summary>
    public const int StandardMaterialEntryCount = 7;

    /// <summary>Distinct GPU textures uploaded (excludes the two defaults) — dedupe metric.</summary>
    public int TextureCount => _textureCache.Count;

    /// <summary>Number of live materials.</summary>
    public int MaterialCount => _materialCount;

    internal Func<bool>? IsFrameInProgress { private get; init; }

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
        try
        {
            _sampler = renderer.CreateSampler(in samplerDesc);
            // Defaults: white drives factor-only materials; normals use a flat tangent-space map.
            _defaultWhite = CreateSolidTexture("PbrDefaultWhite", 255, 255, 255, 255);
            _defaultNormal = CreateSolidTexture("PbrDefaultNormal", 128, 128, 255, 255);
        }
        catch
        {
            if (_defaultWhite.IsValid) renderer.DestroyTexture(_defaultWhite);
            if (_sampler.IsValid) renderer.DestroySampler(_sampler);
            throw;
        }
    }

    /// <summary>Creates GPU resources from material parameters and independently resolved cooked textures.</summary>
    /// <remarks>Texture payloads are consumed synchronously, not retained. Shared uploads live until
    /// the last material releases them. Extra bindings remain caller-owned; target bindings follow
    /// named frame textures automatically. Asset I/O and source-format conversion belong to loaders.</remarks>
    public int AddMaterial(in PbrMaterialDesc material, PbrMaterialTextures textures = default,
        int programId = 0, ReadOnlySpan<BindGroupEntryDesc> extraEntries = default,
        ReadOnlySpan<MaterialTarget> targets = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(material);
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
        var references = new List<TextureKey>(5);
        var group = default(BindGroupHandle);
        var materialId = _materials.Count;
        try
        {
            var baseColor = ResolveTexture(textures.BaseColor.Span, CompressedTextureUsage.ColorSrgb, _defaultWhite, references);
            var metallicRoughness = ResolveTexture(textures.MetallicRoughness.Span, CompressedTextureUsage.LinearData, _defaultWhite, references);
            var normal = ResolveTexture(textures.Normal.Span, CompressedTextureUsage.NormalMap, _defaultNormal, references);
            var occlusion = ResolveTexture(textures.Occlusion.Span, CompressedTextureUsage.LinearData, _defaultWhite, references);
            var emissive = ResolveTexture(textures.Emissive.Span, CompressedTextureUsage.ColorSrgb, _defaultWhite, references);

            var entries = new BindGroupEntryDesc[StandardMaterialEntryCount + extraCount];
            entries[0] = BindGroupEntryDesc.ForBuffer(0, ubo, 0, (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<MaterialUniformsGpu>());
            entries[1] = BindGroupEntryDesc.ForTexture(1, baseColor);
            entries[2] = BindGroupEntryDesc.ForSampler(2, _sampler);
            entries[3] = BindGroupEntryDesc.ForTexture(3, metallicRoughness);
            entries[4] = BindGroupEntryDesc.ForTexture(4, normal);
            entries[5] = BindGroupEntryDesc.ForTexture(5, occlusion);
            entries[6] = BindGroupEntryDesc.ForTexture(6, emissive);
            extras.CopyTo(entries, StandardMaterialEntryCount);
            group = _renderer.CreateBindGroup(new BindGroupDesc($"PbrMaterialGroup[{materialId}]", layout, entries));

            // Transmission needs the alpha-blend pipeline even for AlphaMode=Opaque materials.
            var blend = material.AlphaMode == PbrAlphaMode.Blend || material.TransmissionFactor > 0f;
            var opaque = !blend && material.AlphaMode == PbrAlphaMode.Opaque;
            var entry = new MaterialEntry(ubo, group, blend, programId, entries, layout, references.ToArray(),
                new TraceSurface(material.BaseColorFactor, material.EmissiveFactor, material.MetallicFactor),
                opaque && (programId == 0 || _programOptions[programId].OpaqueCoverage),
                opaque && (programId == 0 || _programOptions[programId].AllowsOpaqueReordering));
            if (bound.Length > 0) _targets.Add(materialId, new TargetSet(bound));
            _materials.Add(entry);
            _materialCount++;
            return materialId;
        }
        catch
        {
            _targets.Remove(materialId);
            if (group.IsValid) _renderer.DestroyBindGroup(group);
            ReleaseTextures(references);
            _renderer.DestroyBuffer(ubo);
            throw;
        }
    }

    /// <summary>Releases a material and its owned resources, returning false for an unknown or released ID.</summary>
    /// <remarks>Remove instances using the material before releasing it; IDs are never reused.
    /// Extra binding resources remain caller-owned, and shared textures survive until their last material is released.</remarks>
    public bool ReleaseMaterial(int materialId)
    {
        if (_disposed || (uint)materialId >= (uint)_materials.Count || _materials[materialId] is not { } material)
            return false;
        if (IsFrameInProgress?.Invoke() == true)
            throw new InvalidOperationException("Cannot release a material while a render frame is in progress.");
        _materials[materialId] = null;
        _materialCount--;
        _targets.Remove(materialId);
        _renderer.DestroyBindGroup(material.Group);
        _renderer.DestroyBuffer(material.Ubo);
        ReleaseTextures(material.Textures);
        return true;
    }

    /// <summary>The frame targets <paramref name="materialId"/> follows, by name. What a pass
    /// drawing the material reads.</summary>
    public ReadOnlySpan<string> TargetsOf(int materialId)
    {
        GetMaterial(materialId);
        return _targets.TryGetValue(materialId, out var set) ? set.Names : default;
    }

    private sealed class TargetSet((MaterialTarget Target, TextureViewHandle Bound)[] bound)
    {
        public (MaterialTarget Target, TextureViewHandle Bound)[] Bound = bound;
        public readonly string[] Names = Array.ConvertAll(bound, static b => b.Target.Target);
    }

    /// <summary>Re-resolve every target-following entry and rebuild the groups whose view changed:
    /// a resize recreated the target, or capture was switched on or off. Once per frame, before
    /// recording.</summary>
    internal void ResolveTargets()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var (materialId, set) in _targets)
        {
            var bound = set.Bound;
            (MaterialTarget Target, TextureViewHandle Bound)[]? updated = null;
            for (var t = 0; t < bound.Length; t++)
            {
                var view = ResolveTarget(bound[t].Target.Target);
                if (view == bound[t].Bound) continue;
                updated ??= ((MaterialTarget Target, TextureViewHandle Bound)[])bound.Clone();
                updated[t].Bound = view;
            }
            if (updated is null) continue;

            var material = GetMaterial(materialId);
            var entries = (BindGroupEntryDesc[])material.Entries.Clone();
            for (var t = 0; t < updated.Length; t++)
                for (var i = StandardMaterialEntryCount; i < entries.Length; i++)
                    if (entries[i].Binding == updated[t].Target.Binding)
                        entries[i] = BindGroupEntryDesc.ForTextureView(entries[i].Binding, updated[t].Bound);
            ReplaceGroup(materialId, material, entries);
            set.Bound = updated;
        }
    }

    // Black while the target does not exist, so a binding never dangles and a shader that
    // samples it anyway reads zero.
    private TextureViewHandle ResolveTarget(string name) =>
        _registry!.Contains(name) ? _registry.View(name) : _registry.View(_registry.Black);

    /// <summary>Replaces an extra material binding and rebuilds its bind group.</summary>
    /// <remarks>Use MaterialTarget for engine targets that should track resize automatically. For
    /// manually bound views, rebind from SceneColorCaptureFeature.ViewChanged; in-flight work
    /// retains the previous native group.</remarks>
    public void UpdateExtraEntry(int materialId, in BindGroupEntryDesc entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var material = GetMaterial(materialId);
        var entries = material.Entries;
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
        var expected = material.Layout.Entries[index];
        if (!EntryKindMatches(entry.Kind, expected.Type))
            throw new ArgumentException(
                $"Extra entry at binding {entry.Binding} supplies a {entry.Kind}, but material program " +
                $"{material.ProgramId} declares a {expected.Type} there.", nameof(entry));

        var updated = (BindGroupEntryDesc[])entries.Clone();
        updated[index] = entry;
        ReplaceGroup(materialId, material, updated);
    }

    private void ReplaceGroup(int materialId, MaterialEntry material, BindGroupEntryDesc[] entries)
    {
        var rebuilt = _renderer.CreateBindGroup(new BindGroupDesc($"PbrMaterialGroup[{materialId}]", material.Layout, entries));
        _materials[materialId] = material with { Group = rebuilt, Entries = entries };
        _renderer.DestroyBindGroup(material.Group);
    }

    /// <summary>The shader program a material draws with — 0 for the built-in PBR program.</summary>
    public int GetProgramId(int materialId) => GetMaterial(materialId).ProgramId;

    internal void RegisterProgramLayout(int programId, in BindGroupLayoutDesc group2Layout,
        MaterialProgramOptions options = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _programGroup2Layouts.Add(programId, group2Layout);
        _programOptions.Add(programId, options);
    }

    internal bool ReleaseProgramLayout(int programId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_programGroup2Layouts.ContainsKey(programId)) return false;
        foreach (var material in _materials)
            if (material?.ProgramId == programId)
                throw new InvalidOperationException($"Material program {programId} still has live materials.");
        _programGroup2Layouts.Remove(programId);
        _programOptions.Remove(programId);
        return true;
    }

    internal bool PreservesMeshBounds(int materialId)
    {
        var programId = GetProgramId(materialId);
        return programId == 0 || _programOptions[programId].PreservesMeshBounds;
    }

    internal bool SupportsInstancing(int materialId)
    {
        var programId = GetProgramId(materialId);
        return programId == 0 || _programOptions[programId].InstancedVertexEntryPoint is not null;
    }

    internal bool AllowsOpaqueReordering(int materialId) => GetMaterial(materialId).Reorderable;

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
        var material = new PbrMaterialDesc
        {
            Name = "default",
            BaseColorFactor = baseColorFactor,
            MetallicFactor = metallic,
            RoughnessFactor = roughness,
        };
        return AddMaterial(in material);
    }

    public BindGroupHandle GetBindGroup(int materialId) => GetMaterial(materialId).Group;

    public bool IsBlend(int materialId) => GetMaterial(materialId).Blend;

    internal bool IsOccluder(int materialId) => GetMaterial(materialId).Occluder;

    /// <summary>The factor-only surface the tracer shades a hit on this material with.</summary>
    public TraceSurface GetTraceSurface(int materialId) => GetMaterial(materialId).Surface;

    private MaterialEntry GetMaterial(int materialId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)materialId >= (uint)_materials.Count || _materials[materialId] is not { } material)
            throw new ArgumentException($"Material {materialId} is unknown or has been released.", nameof(materialId));
        return material;
    }

    private TextureHandle ResolveTexture(
        ReadOnlySpan<byte> ktx2, CompressedTextureUsage usage, TextureHandle fallback,
        List<TextureKey> references)
    {
        if (ktx2.IsEmpty) return fallback;

        // Hashing the (already-small, supercompressed) KTX2 bytes is trivial next to a
        // transcode and buys cross-asset correctness — see the _textureCache comment.
        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ktx2));
        var key = new TextureKey(contentHash, usage);
        if (_textureCache.TryGetValue(key, out var cached))
        {
            references.Add(key);
            cached.References++;
            return cached.Handle;
        }

        var transcoded = _renderer.SupportsBcTextureCompression
            ? Ktx2Transcoder.TranscodeToBc(ktx2, usage)
            : Ktx2Transcoder.TranscodeToRgba32(ktx2, usage);
        if (transcoded.IsEmpty)
        {
            // Malformed payload → the transcoder's empty sentinel → visible-but-wrong default,
            // matching the transcoder contract (no throw at render-load time).
            _textureCache.Add(key, new TextureEntry(fallback, owned: false));
            references.Add(key);
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
        try
        {
            for (var level = 0; level < transcoded.MipLevels.Length; level++)
            {
                var mip = transcoded.MipLevels[level];
                _renderer.WriteTexture(
                    handle, (uint)level,
                    transcoded.Data.AsSpan(mip.Offset, mip.Length),
                    (uint)mip.BytesPerRow, (uint)mip.Rows,
                    (uint)mip.Width, (uint)mip.Height);
            }
        }
        catch
        {
            _renderer.DestroyTexture(handle);
            throw;
        }

        _textureCache.Add(key, new TextureEntry(handle, owned: true));
        references.Add(key);
        return handle;
    }

    private void ReleaseTextures(IEnumerable<TextureKey> references)
    {
        foreach (var key in references)
        {
            var texture = _textureCache[key];
            if (--texture.References != 0) continue;
            _textureCache.Remove(key);
            if (texture.Owned) _renderer.DestroyTexture(texture.Handle);
        }
    }

    private TextureHandle CreateSolidTexture(string name, byte r, byte g, byte b, byte a)
    {
        var desc = new TextureDesc(
            name, 1, 1, 1, 1, 1, TextureDimension.D2,
            TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst);
        var handle = _renderer.CreateTexture(in desc);
        try
        {
            _renderer.WriteTexture(handle, 0, [r, g, b, a], 4, 1, 1, 1);
            return handle;
        }
        catch
        {
            _renderer.DestroyTexture(handle);
            throw;
        }
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
        for (var materialId = 0; materialId < _materials.Count; materialId++) ReleaseMaterial(materialId);
        _disposed = true;
        _materials.Clear();
        _targets.Clear();
        _programGroup2Layouts.Clear();
        _programOptions.Clear();
        _renderer.DestroyTexture(_defaultNormal);
        _renderer.DestroyTexture(_defaultWhite);
        _renderer.DestroySampler(_sampler);
    }
}
