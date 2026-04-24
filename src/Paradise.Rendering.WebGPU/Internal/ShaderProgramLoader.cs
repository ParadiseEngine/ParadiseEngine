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

        // Compute per-binding visibility from the entry points that reference it.
        var visibilityByName = new Dictionary<string, ShaderStage>(StringComparer.Ordinal);
        foreach (var ep in entryPoints)
        {
            if (ep.Bindings is null) continue;
            var stage = ParseStage(ep.Stage);
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
            if (p.Binding?.Kind is not "descriptorTableSlot") continue;
            if (p.Type is null)
                throw new InvalidOperationException($"Shader parameter '{p.Name ?? "<unnamed>"}' has no type node — Slang reflection schema may have changed.");

            var space = p.Binding.Space ?? 0u;
            var (type, minSize) = MapParameterType(p);
            var visibility = p.Name is not null && visibilityByName.TryGetValue(p.Name, out var vis)
                ? vis
                : ShaderStage.Vertex | ShaderStage.Fragment;

            if (!groupedByIndex.TryGetValue(space, out var list))
            {
                list = new List<BindGroupLayoutEntryDesc>();
                groupedByIndex[space] = list;
            }
            list.Add(new BindGroupLayoutEntryDesc(p.Binding.Index, visibility, type, minSize));
        }

        var groups = new BindGroupLayoutDesc[groupedByIndex.Count];
        var gi = 0;
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
                if (shape.StartsWith("texture", StringComparison.Ordinal))
                    return (BindingResourceType.SampledTexture, 0);
                throw new NotSupportedException(
                    $"Paradise.Rendering M2 does not yet support Slang resource baseShape '{shape}'; reserved for later milestone.");
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
