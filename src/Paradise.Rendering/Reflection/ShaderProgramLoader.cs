using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Text.Json;

namespace Paradise.Rendering;

/// <summary>Loads a build-time Slang-compiled shader pair from an assembly's embedded resources
/// (<c>{prefix}.wgsl</c> + <c>{prefix}.reflection.json</c>) and returns a
/// <see cref="ShaderProgramDesc"/> with vertex layout populated from reflection — never
/// hand-coded. The transformation keeps the engine-canonical record shape stable while the loader
/// absorbs any Slang reflection-JSON schema drift.</summary>
/// <remarks>Backend-agnostic on purpose: the WGSL blob and its reflection record are produced by
/// <c>Slang.targets</c> at build time and mean the same thing to every rendering backend, so the
/// loader lives in this contract package rather than inside one of them.</remarks>
public static class ShaderProgramLoader
{
    // Well-known shader parameter names whose bind-group layout must be forced to the shadow-map
    // depth-texture / comparison-sampler kinds (slangc reflection can't express them). Must match
    // the declarations in pbr.slang and the Slang.targets WGSL depth-texture patch.
    private const string ShadowTextureName = "shadowTexture";
    private const string ShadowSamplerName = "shadowSampler";
    private const string PositionTextureName = "positionTexture"; // SSAO world-position pre-pass (Rgba32Float, unfilterable)
    // The opaque scene depth read by the capture blit (Depth32Float viewed as unfilterable float,
    // textureLoad only — WebGPU allows depth formats under the unfilterable-float sample type).
    private const string CaptureDepthTextureName = "captureDepthTexture";

    /// <summary>Load <paramref name="logicalNamePrefix"/>.wgsl + .reflection.json from
    /// <paramref name="assembly"/>. Returns a <see cref="ShaderProgramDesc"/> with one
    /// <see cref="ShaderModuleDesc"/> per Slang entry point (each carrying the same WGSL blob; the
    /// entry point name + stage selects what the WebGPU shader stage compiles), and one
    /// <see cref="VertexBufferLayoutDesc"/> built from the vertex entry point's input struct.</summary>
    public static ShaderProgramDesc Load(Assembly assembly, string logicalNamePrefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (string.IsNullOrEmpty(logicalNamePrefix)) throw new ArgumentException("Prefix required.", nameof(logicalNamePrefix));

        var wgsl = ReadResourceString(assembly, logicalNamePrefix + ".wgsl");
        var reflectionJson = ReadResourceString(assembly, logicalNamePrefix + ".reflection.json");

        var reflection = JsonSerializer.Deserialize(reflectionJson, SlangReflectionJsonContext.Default.SlangReflection)
            ?? throw new InvalidOperationException($"Reflection JSON for '{logicalNamePrefix}' deserialized to null.");

        return BuildProgramDesc(wgsl, reflection);
    }

    private static string ReadResourceString(Assembly assembly, string logicalName)
    {
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{logicalName}' not found in '{assembly.GetName().Name}'. " +
                $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Translate a Slang reflection record into a <see cref="ShaderProgramDesc"/>. Public
    /// for direct testing; the assembly-loading path above is a thin wrapper.</summary>
    internal static ShaderProgramDesc BuildProgramDesc(string wgsl, SlangReflection reflection)
    {
        var entryPoints = reflection.EntryPoints ?? Array.Empty<SlangEntryPoint>();

        var modules = new ShaderModuleDesc[entryPoints.Length];
        for (var i = 0; i < entryPoints.Length; i++)
        {
            var ep = entryPoints[i];
            modules[i] = new ShaderModuleDesc(wgsl, ep.Name, ParseStage(ep.Stage));
        }

        var vertexBuffers = ExtractVertexBuffers(entryPoints);
        var byEntryPoint = new Dictionary<string, VertexBufferLayoutDesc[]>(StringComparer.Ordinal);
        foreach (var ep in entryPoints)
        {
            if (!string.Equals(ep.Stage, "vertex", StringComparison.Ordinal)) continue;
            byEntryPoint[ep.Name] = ExtractVertexBuffers([ep]);
        }
        // Visibility follows the file's stage mix (slangc lists ALL globals per entry point, so
        // per-binding stage attribution is unavailable): compute-only files get Compute, raster
        // files keep the historical Vertex|Fragment (byte-identical layouts to before compute
        // existed), and mixed files get the union. RW entries are special-cased inside
        // BuildLayout — write-access storage is never legal with Vertex visibility.
        var hasCompute = false;
        var hasRaster = false;
        foreach (var ep in entryPoints)
        {
            if (string.Equals(ep.Stage, "compute", StringComparison.Ordinal)) hasCompute = true;
            else hasRaster = true;
        }
        var defaultVisibility = (hasCompute, hasRaster) switch
        {
            (true, false) => ShaderStage.Compute,
            (false, _) => ShaderStage.Vertex | ShaderStage.Fragment,
            (true, true) => ShaderStage.Vertex | ShaderStage.Fragment | ShaderStage.Compute,
        };
        var (layout, uniformBlocks) = BuildLayout(
            reflection.Parameters ?? Array.Empty<SlangParameter>(), defaultVisibility, hasCompute);

        return new ShaderProgramDesc(modules, layout, vertexBuffers)
        {
            UniformBlocks = uniformBlocks,
            VertexBuffersByEntryPoint = byEntryPoint,
        };
    }

    /// <summary>Build bind-group layouts + uniform-block byte layouts from the reflection's
    /// top-level global parameters. Bindings are grouped by descriptor space (bind group),
    /// ordered by binding index within each group. Visibility is
    /// <paramref name="defaultVisibility"/> for every entry (slangc's per-entry-point "bindings"
    /// lists ALL globals rather than per-stage usage, and over-visible bindings are valid
    /// WebGPU), except write-access storage resources, which drop Vertex visibility — WebGPU
    /// rejects write storage in the vertex stage.</summary>
    private static (PipelineLayoutDesc Layout, UniformBlockDesc[] UniformBlocks) BuildLayout(
        SlangParameter[] parameters, ShaderStage defaultVisibility, bool hasCompute)
    {
        if (parameters.Length == 0)
        {
            return (new PipelineLayoutDesc(
                Groups: Array.Empty<BindGroupLayoutDesc>(),
                PushConstants: Array.Empty<PushConstantRangeDesc>()), Array.Empty<UniformBlockDesc>());
        }

        var groups = new SortedDictionary<uint, List<BindGroupLayoutEntryDesc>>();
        var uniformBlocks = new List<UniformBlockDesc>();

        foreach (var p in parameters)
        {
            var binding = p.Binding ?? throw new InvalidOperationException(
                $"Global shader parameter '{p.Name ?? "<unnamed>"}' has no binding — Slang reflection schema may have changed.");
            if (binding.Kind != "descriptorTableSlot")
            {
                throw new NotSupportedException(
                    $"Global shader parameter '{p.Name ?? "<unnamed>"}' has binding kind '{binding.Kind ?? "<null>"}'. " +
                    "Only descriptor-table bindings (ConstantBuffer/Texture2D/SamplerState) are supported; " +
                    "push constants and other binding kinds are not plumbed through.");
            }

            var group = binding.Space ?? 0; // slangc omits `space` for group 0
            var type = p.Type ?? throw new InvalidOperationException(
                $"Global shader parameter '{p.Name ?? "<unnamed>"}' has no type node.");

            // slangc reflection cannot distinguish a shadow-map depth texture / comparison sampler
            // from ordinary ones (SamplerComparisonState reflects as plain "samplerState", and the
            // depth Texture2DArray<float> as a plain "texture2DArray float"). The generated WGSL,
            // however, declares them as texture_depth_2d_array / sampler_comparison (the shadowTexture
            // type is patched at build time — see Slang.targets). The bind-group LAYOUT must match the
            // shader, so override by the well-known names.
            // Write-access storage may never carry Vertex visibility (WebGPU validation error);
            // give it Compute when the file has a compute entry, Fragment otherwise (fragment
            // storage writes are legal for write-only access). DELIBERATE CARVE-OUT from the
            // defaultVisibility union: in a mixed compute+raster file an RW resource gets
            // Compute-ONLY visibility, so a fragment stage writing the same storage resource a
            // compute kernel uses would be rejected at pipeline creation — keep such a shader in
            // its own file (or widen this to Compute|Fragment when the first real case lands).
            var writeVisibility = hasCompute ? ShaderStage.Compute : ShaderStage.Fragment;
            var isRw = type.Access is "write" or "readWrite";
            var entry = type.Kind switch
            {
                "constantBuffer" => BuildConstantBufferEntry(p, binding, type, group, uniformBlocks, defaultVisibility),
                "resource" when p.Name == ShadowTextureName => new BindGroupLayoutEntryDesc(
                    binding.Index, ShaderStage.Fragment, BindingResourceType.DepthTextureArray),
                "resource" when p.Name == PositionTextureName => new BindGroupLayoutEntryDesc(
                    binding.Index, ShaderStage.Fragment, BindingResourceType.UnfilterableFloatTexture),
                "resource" when p.Name == CaptureDepthTextureName => new BindGroupLayoutEntryDesc(
                    binding.Index, ShaderStage.Fragment, BindingResourceType.UnfilterableFloatTexture),
                // WTexture2D / RWTexture2D → WGSL texture_storage_2d<format, access>. The
                // [format("...")] attribute is REQUIRED: without it slangc silently defaults the
                // WGSL to rgba32float while reflecting no format at all, and a guessed layout
                // would mismatch the shader.
                "resource" when type.BaseShape == "texture2D" && isRw => new BindGroupLayoutEntryDesc(
                    binding.Index, writeVisibility, BindingResourceType.StorageTexture,
                    StorageFormat: ParseStorageFormat(p),
                    Access: type.Access == "readWrite" ? StorageTextureAccess.ReadWrite : StorageTextureAccess.WriteOnly),
                "resource" when type.BaseShape == "texture2D" => new BindGroupLayoutEntryDesc(
                    binding.Index, defaultVisibility, BindingResourceType.SampledTexture),
                // RWStructuredBuffer<T> → WGSL var<storage, read_write>.
                "resource" when type.BaseShape == "structuredBuffer" && isRw => new BindGroupLayoutEntryDesc(
                    binding.Index, writeVisibility, BindingResourceType.StorageBuffer),
                // StructuredBuffer<T> → WGSL var<storage, read>. Default visibility like every
                // other over-visible entry: read-only storage is legal in the vertex stage (only
                // read_write is prohibited there), and the joint-palette buffer is READ from it.
                // This was Fragment-only once, sized to the Forward+ cluster masks — and a
                // vertex-stage reader then failed createRenderPipeline, which Dawn reports only
                // through the async error callback: the pipeline just silently dropped every
                // frame that used it.
                "resource" when type.BaseShape == "structuredBuffer" => new BindGroupLayoutEntryDesc(
                    binding.Index, defaultVisibility, BindingResourceType.ReadonlyStorageBuffer),
                "samplerState" when p.Name == ShadowSamplerName => new BindGroupLayoutEntryDesc(
                    binding.Index, ShaderStage.Fragment, BindingResourceType.ComparisonSampler),
                "samplerState" => new BindGroupLayoutEntryDesc(
                    binding.Index, defaultVisibility, BindingResourceType.Sampler),
                _ => throw new NotSupportedException(
                    $"Global shader parameter '{p.Name ?? "<unnamed>"}' has unsupported type kind " +
                    $"'{type.Kind}'{(type.BaseShape is null ? "" : $" (baseShape '{type.BaseShape}')")}. " +
                    "Supported: ConstantBuffer<T>, Texture2D, (RW)StructuredBuffer, (R)WTexture2D, SamplerState."),
            };

            if (!groups.TryGetValue(group, out var entries))
            {
                entries = new List<BindGroupLayoutEntryDesc>();
                groups[group] = entries;
            }
            entries.Add(entry);
        }

        var groupDescs = new BindGroupLayoutDesc[groups.Count];
        var g = 0;
        foreach (var (groupIndex, entries) in groups)
        {
            entries.Sort(static (a, b) => a.Binding.CompareTo(b.Binding));
            groupDescs[g++] = new BindGroupLayoutDesc(groupIndex, entries.ToArray());
        }

        var layout = new PipelineLayoutDesc(groupDescs, Array.Empty<PushConstantRangeDesc>());
        return (layout, uniformBlocks.ToArray());
    }

    /// <summary>Map the GLSL image-format spelling of a storage texture's [format] attribute to
    /// the storage-capable engine formats. Missing attribute → throw: slangc would have silently
    /// defaulted the WGSL to rgba32float, and a guessed layout mismatches the shader.</summary>
    private static TextureFormat ParseStorageFormat(SlangParameter parameter) => parameter.Format switch
    {
        "rgba8" => TextureFormat.Rgba8Unorm,
        "rgba16f" => TextureFormat.Rgba16Float,
        "rgba32f" => TextureFormat.Rgba32Float,
        "r32f" => TextureFormat.R32Float,
        null => throw new NotSupportedException(
            $"Storage texture '{parameter.Name ?? "<unnamed>"}' has no [format(\"...\")] attribute. " +
            "Annotate it (e.g. [format(\"rgba16f\")]) — without the attribute slangc silently emits " +
            "rgba32float WGSL while reflecting no format, so the layout cannot be derived."),
        _ => throw new NotSupportedException(
            $"Storage texture '{parameter.Name ?? "<unnamed>"}' uses unsupported [format(\"{parameter.Format}\")]. " +
            "Supported: rgba8, rgba16f, rgba32f, r32f."),
    };

    private static BindGroupLayoutEntryDesc BuildConstantBufferEntry(
        SlangParameter parameter,
        SlangBinding binding,
        SlangTypeNode type,
        uint group,
        List<UniformBlockDesc> uniformBlocks,
        ShaderStage visibility)
    {
        // Total GPU size of the buffer contents lives on the element var layout's uniform binding.
        var sizeBytes = type.ElementVarLayout?.Binding is { Kind: "uniform" } elementBinding
            ? elementBinding.Size ?? 0
            : 0;
        if (sizeBytes == 0)
        {
            throw new InvalidOperationException(
                $"ConstantBuffer '{parameter.Name ?? "<unnamed>"}' has no elementVarLayout uniform size — " +
                "Slang reflection schema may have changed.");
        }

        // One flat field list: struct members with their reflected offsets; array members appear
        // once with Size = total (elementStride × count). Consumers validating mirror structs
        // match on (name, offset, size).
        var fields = Array.Empty<UniformFieldDesc>();
        if (type.ElementType?.Fields is { Length: > 0 } srcFields)
        {
            fields = new UniformFieldDesc[srcFields.Length];
            for (var i = 0; i < srcFields.Length; i++)
            {
                var f = srcFields[i];
                var fb = f.Binding;
                if (fb is not { Kind: "uniform" } || fb.Offset is null || fb.Size is null)
                {
                    throw new InvalidOperationException(
                        $"ConstantBuffer '{parameter.Name}' field '{f.Name}' has no uniform offset/size — " +
                        "Slang reflection schema may have changed.");
                }
                fields[i] = new UniformFieldDesc(f.Name, fb.Offset.Value, fb.Size.Value);
            }
        }

        uniformBlocks.Add(new UniformBlockDesc(
            parameter.Name ?? $"cbuffer_{group}_{binding.Index}",
            group,
            binding.Index,
            sizeBytes,
            fields));

        return new BindGroupLayoutEntryDesc(
            binding.Index,
            visibility,
            BindingResourceType.UniformBuffer,
            MinBufferSize: sizeBytes);
    }

    private static ShaderStage ParseStage(string stage) => stage switch
    {
        "vertex" => ShaderStage.Vertex,
        "fragment" => ShaderStage.Fragment,
        "compute" => ShaderStage.Compute,
        _ => throw new InvalidOperationException($"Unknown Slang stage '{stage}'."),
    };

    private static VertexBufferLayoutDesc[] ExtractVertexBuffers(SlangEntryPoint[] entryPoints)
    {
        SlangEntryPoint? vs = null;
        foreach (var ep in entryPoints)
        {
            if (string.Equals(ep.Stage, "vertex", StringComparison.Ordinal))
            {
                vs = ep;
                break;
            }
        }
        if (vs is null || vs.Parameters is null || vs.Parameters.Length == 0)
        {
            return Array.Empty<VertexBufferLayoutDesc>();
        }

        SlangParameter? vertexInput = null;
        foreach (var p in vs.Parameters)
        {
            if (p.Binding?.Kind == "varyingInput" && p.Type?.Kind == "struct")
            {
                vertexInput = p;
                break;
            }
        }
        if (vertexInput is null) return Array.Empty<VertexBufferLayoutDesc>();

        var fields = vertexInput.Type!.Fields ?? Array.Empty<SlangField>();
        var attributes = new VertexAttributeDesc[fields.Length];
        ulong offset = 0;
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            var format = MapVertexFieldType(field.Type);
            var location = field.Binding?.Index
                ?? throw new InvalidOperationException(
                    $"Vertex field '{field.Name}' has no varyingInput binding — Slang reflection schema may have changed.");
            attributes[i] = new VertexAttributeDesc(location, format, offset);
            offset += VertexFormats.ByteSize(format);
        }

        return new[]
        {
            new VertexBufferLayoutDesc(
                Stride: offset,
                StepMode: VertexStepMode.Vertex,
                Attributes: attributes),
        };
    }

    private static VertexFormat MapVertexFieldType(SlangTypeNode? type)
    {
        if (type is null)
            throw new InvalidOperationException("Vertex field has no Slang type node.");

        return type.Kind switch
        {
            "vector" => MapVector(type),
            "scalar" => MapScalar(type.ScalarType, count: 1),
            _ => throw new InvalidOperationException(
                $"Unsupported vertex field kind '{type.Kind}'. Expected 'scalar' or 'vector'."),
        };
    }

    private static VertexFormat MapVector(SlangTypeNode vector)
    {
        var count = vector.ElementCount
            ?? throw new InvalidOperationException("Vector type missing 'elementCount'.");
        var elementScalar = vector.ElementType?.ScalarType
            ?? throw new InvalidOperationException("Vector type missing 'elementType.scalarType'.");
        return MapScalar(elementScalar, (int)count);
    }

    private static VertexFormat MapScalar(string? scalarType, int count) => (scalarType, count) switch
    {
        ("float32", 1) => VertexFormat.Float32,
        ("float32", 2) => VertexFormat.Float32x2,
        ("float32", 3) => VertexFormat.Float32x3,
        ("float32", 4) => VertexFormat.Float32x4,
        ("int32", 1) => VertexFormat.Sint32,
        ("int32", 2) => VertexFormat.Sint32x2,
        ("int32", 3) => VertexFormat.Sint32x3,
        ("int32", 4) => VertexFormat.Sint32x4,
        ("uint32", 1) => VertexFormat.Uint32,
        ("uint32", 2) => VertexFormat.Uint32x2,
        ("uint32", 3) => VertexFormat.Uint32x3,
        ("uint32", 4) => VertexFormat.Uint32x4,
        _ => throw new InvalidOperationException(
            $"Unsupported scalar type '{scalarType}' x {count} for vertex attribute."),
    };
}
