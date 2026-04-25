using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Loads a build-time Slang-compiled shader pair from an assembly's embedded resources
/// (<c>{prefix}.wgsl</c> + <c>{prefix}.reflection.json</c>) and returns a
/// <see cref="ShaderProgramDesc"/> with vertex layout populated from reflection — never
/// hand-coded. The transformation keeps the engine-canonical record shape stable while the loader
/// absorbs any Slang reflection-JSON schema drift.</summary>
internal static class ShaderProgramLoader
{
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

        // M2: build bind group layouts from module-level `parameters[]` (which carry
        // binding.space + binding.index + the resource type) cross-referenced with
        // entry-point-level `bindings[]` by name to derive per-binding visibility. Stage is
        // inferred from the entry point that references the binding.
        var layout = BuildPipelineLayout(reflection.Parameters ?? Array.Empty<SlangParameter>(), entryPoints);

        return new ShaderProgramDesc(modules, layout, vertexBuffers);
    }

    private static PipelineLayoutDesc BuildPipelineLayout(SlangParameter[] parameters, SlangEntryPoint[] entryPoints)
    {
        if (parameters.Length == 0)
        {
            return new PipelineLayoutDesc(
                Groups: Array.Empty<BindGroupLayoutDesc>(),
                PushConstants: Array.Empty<PushConstantRangeDesc>());
        }

        // Compute per-binding visibility from the entry points that reference it. Slang's
        // `entryPoints[].bindings[]` array conservatively lists every module-level parameter the
        // entry point may touch (not just ones it actually reads), so this union closely tracks
        // the real shader visibility.
        var visibilityByName = new Dictionary<string, ShaderStage>(StringComparer.Ordinal);
        ShaderStage allStages = ShaderStage.None;
        foreach (var ep in entryPoints)
        {
            var stage = ParseStage(ep.Stage);
            allStages |= stage;
            if (ep.Bindings is null) continue;
            foreach (var b in ep.Bindings)
            {
                if (b.Name is null) continue;
                visibilityByName.TryGetValue(b.Name, out var existing);
                visibilityByName[b.Name] = existing | stage;
            }
        }

        var groupedByIndex = new SortedDictionary<uint, List<BindGroupLayoutEntryDesc>>();
        foreach (var p in parameters)
        {
            var kind = p.Binding?.Kind;
            // Fail-fast on bindings the loader recognizes but does not support yet — push constants
            // (Slang `pushConstantBuffer`, empirically verified against slangc -reflection-json
            // v2026.7) are reserved for a later milestone, and silently dropping them would leave
            // the engine unable to validate the shader against its actual bindings. Symmetric with
            // the BindingResourceType reject in MapParameterType and with the
            // BuildNativePipeline push-constant guard.
            if (kind == "pushConstantBuffer")
                throw new NotSupportedException(
                    $"Shader parameter '{p.Name ?? "<unnamed>"}' uses Slang binding kind 'pushConstantBuffer'; " +
                    "push constants are reserved for a later milestone in Paradise.Rendering.");
            // varyingInput / varyingOutput on top-level parameters are vertex-stage I/O, not bind
            // group bindings — handled by ExtractVertexBuffers via entry-point parameters and not
            // emitted at module scope by slangc, but tolerate them defensively.
            if (kind is "varyingInput" or "varyingOutput") continue;
            if (kind != "descriptorTableSlot")
                throw new NotSupportedException(
                    $"Shader parameter '{p.Name ?? "<unnamed>"}' uses unrecognized Slang binding kind '{kind ?? "<null>"}'. " +
                    "Paradise.Rendering's reflection loader only handles 'descriptorTableSlot' for module-level " +
                    "resource bindings; new Slang reflection kinds need explicit handling here.");
            if (p.Type is null)
                throw new InvalidOperationException($"Shader parameter '{p.Name ?? "<unnamed>"}' has no type node — Slang reflection schema may have changed.");

            var binding = p.Binding!;
            var space = binding.Space ?? 0u;
            var (type, minSize) = MapParameterType(p);
            // Visibility comes from the entry-point bindings table by name. If a parameter doesn't
            // appear in any entry-point's bindings (unusual — Slang lists them conservatively),
            // fall back to the union of every entry point's stage rather than hardcoding
            // Vertex|Fragment, so a future compute-only program doesn't get over-permissioned.
            var visibility = p.Name is not null && visibilityByName.TryGetValue(p.Name, out var vis)
                ? vis
                : allStages;

            if (!groupedByIndex.TryGetValue(space, out var list))
            {
                list = new List<BindGroupLayoutEntryDesc>();
                groupedByIndex[space] = list;
            }
            list.Add(new BindGroupLayoutEntryDesc(binding.Index, visibility, type, minSize));
        }

        // M2 requires dense bind group spaces (0, 1, 2, ...) — `WebGpuDevice.BuildNativePipeline`
        // packs the user-supplied `BindGroupLayouts` densely into the WebGPU pipeline-layout
        // descriptor by array position (position N becomes @group(N)). Sparse Slang spaces (e.g.,
        // space0 + space2 with a gap at 1) would silently misalign with `@group(2)` references in
        // the WGSL output. Detect the gap at load time and surface a clear deferral message
        // rather than building a misaligned pipeline; auto-padding with placeholder layouts is
        // tracked as a follow-up.
        var gi = 0;
        foreach (var kv in groupedByIndex)
        {
            if (kv.Key != (uint)gi)
                throw new NotSupportedException(
                    $"Paradise.Rendering M2 requires dense Slang register spaces starting at 0; " +
                    $"shader has a gap at space {gi} (next reflected space is {kv.Key}). " +
                    "Sparse spaces with auto-padding are reserved for a later milestone.");
            gi++;
        }

        var groups = new BindGroupLayoutDesc[groupedByIndex.Count];
        gi = 0;
        foreach (var kv in groupedByIndex)
        {
            kv.Value.Sort(static (a, b) => a.Binding.CompareTo(b.Binding));
            groups[gi++] = new BindGroupLayoutDesc(kv.Key, kv.Value.ToArray());
        }

        return new PipelineLayoutDesc(
            Groups: groups,
            PushConstants: Array.Empty<PushConstantRangeDesc>());
    }

    private static (BindingResourceType Type, ulong MinBufferSize) MapParameterType(SlangParameter p)
    {
        var t = p.Type!;
        switch (t.Kind)
        {
            case "constantBuffer":
            {
                // std140-padded size lives in elementVarLayout.binding.size for uniform buffers.
                // When absent (unusual), fall back to 0 meaning "no minimum" — Dawn validates
                // size at bind time against the actual buffer.
                ulong minSize = t.ElementVarLayout?.Binding?.Size ?? 0u;
                return (BindingResourceType.UniformBuffer, minSize);
            }
            case "parameterBlock":
                return (BindingResourceType.UniformBuffer, 0);
            case "resource":
            {
                var shape = t.BaseShape ?? "<null>";
                // M2 binds Texture2D only — the native bind-group-layout build hardcodes
                // SampleType=Float / ViewDimension=D2 / Multisampled=false. Cube / D2Array /
                // D3 / depth / integer-sampled textures need fanout in BindingResourceType
                // before the layout build can route them correctly. Reject everything else
                // here so a non-Texture2D shader fails at load-time instead of building a
                // silently-incorrect WgBindGroupLayoutEntry downstream.
                if (shape == "texture2D")
                    return (BindingResourceType.SampledTexture, 0);
                throw new NotSupportedException(
                    $"Paradise.Rendering M2 only supports Texture2D bindings; Slang resource baseShape '{shape}' " +
                    "(cube / array / 3D / depth / integer-sampled textures) is reserved for a later milestone.");
            }
            case "samplerState":
                return (BindingResourceType.Sampler, 0);
            case "structuredBuffer":
                return (BindingResourceType.ReadonlyStorageBuffer, 0);
            case "rwStructuredBuffer":
                return (BindingResourceType.StorageBuffer, 0);
            default:
                throw new NotSupportedException(
                    $"Paradise.Rendering M2 does not yet support Slang parameter type kind '{t.Kind}' (parameter '{p.Name ?? "<unnamed>"}'); reserved for later milestone.");
        }
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
