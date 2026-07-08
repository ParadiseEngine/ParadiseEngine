using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Text.Json;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Loads a build-time Slang-compiled shader pair from an assembly's embedded resources
/// (<c>{prefix}.wgsl</c> + <c>{prefix}.reflection.json</c>) and returns a
/// <see cref="ShaderProgramDesc"/> with vertex layout populated from reflection — never
/// hand-coded. The transformation keeps the engine-canonical record shape stable while the loader
/// absorbs any Slang reflection-JSON schema drift.</summary>
internal static class ShaderProgramLoader
{
    // Well-known shader parameter names whose bind-group layout must be forced to the shadow-map
    // depth-texture / comparison-sampler kinds (slangc reflection can't express them). Must match
    // the declarations in pbr.slang and the Slang.targets WGSL depth-texture patch.
    private const string ShadowTextureName = "shadowTexture";
    private const string ShadowSamplerName = "shadowSampler";

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
        var (layout, uniformBlocks) = BuildLayout(reflection.Parameters ?? Array.Empty<SlangParameter>());

        return new ShaderProgramDesc(modules, layout, vertexBuffers) { UniformBlocks = uniformBlocks };
    }

    /// <summary>Build bind-group layouts + uniform-block byte layouts from the reflection's
    /// top-level global parameters. Bindings are grouped by descriptor space (bind group),
    /// ordered by binding index within each group. Visibility is Vertex|Fragment for every
    /// entry: slangc's per-entry-point "bindings" lists ALL globals rather than per-stage usage
    /// (verified against real output), and over-visible bindings are valid WebGPU.</summary>
    private static (PipelineLayoutDesc Layout, UniformBlockDesc[] UniformBlocks) BuildLayout(SlangParameter[] parameters)
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
            var entry = type.Kind switch
            {
                "constantBuffer" => BuildConstantBufferEntry(p, binding, type, group, uniformBlocks),
                "resource" when p.Name == ShadowTextureName => new BindGroupLayoutEntryDesc(
                    binding.Index, ShaderStage.Fragment, BindingResourceType.DepthTextureArray),
                "resource" when type.BaseShape == "texture2D" => new BindGroupLayoutEntryDesc(
                    binding.Index, ShaderStage.Vertex | ShaderStage.Fragment, BindingResourceType.SampledTexture),
                "samplerState" when p.Name == ShadowSamplerName => new BindGroupLayoutEntryDesc(
                    binding.Index, ShaderStage.Fragment, BindingResourceType.ComparisonSampler),
                "samplerState" => new BindGroupLayoutEntryDesc(
                    binding.Index, ShaderStage.Vertex | ShaderStage.Fragment, BindingResourceType.Sampler),
                _ => throw new NotSupportedException(
                    $"Global shader parameter '{p.Name ?? "<unnamed>"}' has unsupported type kind " +
                    $"'{type.Kind}'{(type.BaseShape is null ? "" : $" (baseShape '{type.BaseShape}')")}. " +
                    "Supported: ConstantBuffer<T>, Texture2D, SamplerState."),
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

    private static BindGroupLayoutEntryDesc BuildConstantBufferEntry(
        SlangParameter parameter,
        SlangBinding binding,
        SlangTypeNode type,
        uint group,
        List<UniformBlockDesc> uniformBlocks)
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
            ShaderStage.Vertex | ShaderStage.Fragment,
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
