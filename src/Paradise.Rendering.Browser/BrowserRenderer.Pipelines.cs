using System;
using System.Runtime.Versioning;
using System.Text;

namespace Paradise.Rendering.Browser;

/// <summary>Pipeline construction: entry-point selection, vertex-layout and bind-group-layout
/// serialization. Deliberately mirrors <c>WebGpuDevice.BuildNativePipeline</c> decision for
/// decision — a program must produce the same pipeline in a browser tab as it does under Dawn, or
/// the two backends drift into "works on desktop only" territory.</summary>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserRenderer
{
    /// <inheritdoc/>
    public PipelineHandle CreatePipeline(
        in ShaderProgramDesc program,
        TextureFormat colorFormat,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        IndexFormat stripIndexFormat = IndexFormat.Uint16,
        TextureFormat? depthStencilFormat = null,
        BlendMode blend = BlendMode.Opaque,
        bool depthWriteEnabled = true,
        CompareFunction depthCompare = CompareFunction.Less,
        string? fragmentEntryPoint = null,
        string? vertexEntryPoint = null)
    {
        ThrowIfDisposed();

        var vsModule = SelectModule(program, ShaderStage.Vertex, vertexEntryPoint);
        if (vsModule is null)
            throw new InvalidOperationException(vertexEntryPoint is null
                ? "ShaderProgramDesc has no vertex module."
                : $"ShaderProgramDesc has no vertex module named '{vertexEntryPoint}'.");
        var fsModule = SelectModule(program, ShaderStage.Fragment, fragmentEntryPoint);
        if (fsModule is null)
            throw new InvalidOperationException(fragmentEntryPoint is null
                ? "ShaderProgramDesc has no fragment module."
                : $"ShaderProgramDesc has no fragment module named '{fragmentEntryPoint}'.");

        // The chosen entry point's own vertex layout, falling back to the program-level one for
        // single-vertex-entry programs. Selecting a module without its layout feeds one shader's
        // stride to another's attributes, which draws nothing and reports nothing.
        var vertexLayouts = program.VertexBuffersByEntryPoint.TryGetValue(vsModule.EntryPoint, out var perEntry)
            ? perEntry
            : program.VertexBuffers;

        var json = new StringBuilder(1024);
        json.Append("{\"label\":\"ShaderProgramPipeline\",\"vs\":").Append(GetOrCreateShaderModule(vsModule))
            .Append(",\"vsEntry\":");
        AppendJsonString(json, vsModule.EntryPoint);
        json.Append(",\"fs\":").Append(GetOrCreateShaderModule(fsModule)).Append(",\"fsEntry\":");
        AppendJsonString(json, fsModule.EntryPoint);
        json.Append(",\"colorFormat\":\"").Append(FormatName(colorFormat)).Append('"')
            .Append(",\"blend\":").Append((int)blend);
        AppendPrimitive(json, topology, stripIndexFormat);
        AppendDepthState(json, depthStencilFormat, depthWriteEnabled, depthCompare);
        AppendVertexLayouts(json, vertexLayouts);
        AppendPipelineLayout(json, program.Layout);
        json.Append('}');

        return RegisterPipeline(json.ToString(), depthStencilFormat is not null);
    }

    /// <inheritdoc/>
    public PipelineHandle CreateDepthOnlyPipeline(
        in ShaderProgramDesc program,
        TextureFormat depthStencilFormat,
        ReadOnlyMemory<VertexBufferLayoutDesc> vertexLayouts,
        CompareFunction depthCompare = CompareFunction.Less,
        string? vertexEntryPoint = null)
    {
        ThrowIfDisposed();

        var vsModule = SelectModule(program, ShaderStage.Vertex, vertexEntryPoint);
        if (vsModule is null)
            throw new InvalidOperationException(vertexEntryPoint is null
                ? "Depth-only program has no vertex module."
                : $"Depth-only program has no vertex module named '{vertexEntryPoint}'.");

        var json = new StringBuilder(512);
        json.Append("{\"label\":\"DepthOnlyPipeline\",\"vs\":").Append(GetOrCreateShaderModule(vsModule))
            .Append(",\"vsEntry\":");
        AppendJsonString(json, vsModule.EntryPoint);
        // No fragment stage and therefore no color target: the shadow-caster shape. WebGPU accepts
        // it as long as a depth-stencil state is present.
        json.Append(",\"fs\":-1,\"fsEntry\":\"\",\"colorFormat\":null,\"blend\":0");
        AppendPrimitive(json, PrimitiveTopology.TriangleList, IndexFormat.Uint16);
        AppendDepthState(json, depthStencilFormat, depthWriteEnabled: true, depthCompare);
        AppendVertexLayouts(json, vertexLayouts.Span);
        AppendPipelineLayout(json, program.Layout);
        json.Append('}');

        return RegisterPipeline(json.ToString(), hasDepth: true);
    }

    /// <inheritdoc/>
    public ComputePipelineHandle CreateComputePipeline(in ShaderProgramDesc program, string? entryPoint = null)
    {
        ThrowIfDisposed();

        var csModule = SelectModule(program, ShaderStage.Compute, entryPoint);
        if (csModule is null)
            throw new InvalidOperationException(entryPoint is null
                ? "ShaderProgramDesc has no compute module."
                : $"ShaderProgramDesc has no compute module named '{entryPoint}'.");

        var json = new StringBuilder(512);
        json.Append("{\"label\":\"ComputePipeline\",\"cs\":").Append(GetOrCreateShaderModule(csModule))
            .Append(",\"csEntry\":");
        AppendJsonString(json, csModule.EntryPoint);
        AppendPipelineLayout(json, program.Layout);
        json.Append('}');

        var slot = _computePipelines.Allocate(out var generation);
        CreateComputePipelineJs((int)slot, json.ToString());
        return new ComputePipelineHandle(slot, generation);
    }

    /// <inheritdoc/>
    public void DestroyComputePipeline(ComputePipelineHandle handle)
    {
        ThrowIfDisposed();
        if (!_computePipelines.Release(handle.Index, handle.Generation)) return;
        DestroyComputePipelineJs((int)handle.Index);
    }

    /// <inheritdoc/>
    public void DestroyPipeline(PipelineHandle handle)
    {
        ThrowIfDisposed();
        if (!_pipelines.Release(handle.Index, handle.Generation)) return;
        _pipelineHasDepth.Remove(handle);
        DestroyPipelineJs((int)handle.Index);
    }

    private PipelineHandle RegisterPipeline(string descJson, bool hasDepth)
    {
        var slot = _pipelines.Allocate(out var generation);
        CreatePipelineJs((int)slot, descJson);
        var handle = new PipelineHandle(slot, generation);
        _pipelineHasDepth[handle] = hasDepth;
        return handle;
    }

    // Without a selector the FIRST module of the stage wins; with one, the module whose entry
    // point matches by name. Same rule as the Dawn backend — taking the LAST module instead
    // silently repoints every existing pipeline the moment a program grows a second entry point.
    private static ShaderModuleDesc? SelectModule(in ShaderProgramDesc program, ShaderStage stage, string? entryPoint)
    {
        ShaderModuleDesc? selected = null;
        foreach (var module in program.Modules)
        {
            if ((module.Stage & stage) == 0) continue;
            if (entryPoint is null)
            {
                selected ??= module;
            }
            else if (string.Equals(module.EntryPoint, entryPoint, StringComparison.Ordinal))
            {
                selected = module;
            }
        }
        return selected;
    }

    private int GetOrCreateShaderModule(ShaderModuleDesc module)
    {
        if (_shaderModules.TryGetValue(module.Wgsl, out var slot)) return slot;
        slot = _nextShaderModuleSlot++;
        CreateShaderModuleJs(slot, module.Wgsl, module.EntryPoint);
        _shaderModules[module.Wgsl] = slot;
        return slot;
    }

    private static void AppendPrimitive(StringBuilder json, PrimitiveTopology topology, IndexFormat stripIndexFormat)
    {
        json.Append(",\"topology\":\"").Append(TopologyName(topology)).Append('"');
        // A strip index format is only legal on a strip topology, and required there.
        json.Append(",\"stripIndexFormat\":");
        if (topology is PrimitiveTopology.LineStrip or PrimitiveTopology.TriangleStrip)
            json.Append('"').Append(stripIndexFormat == IndexFormat.Uint16 ? "uint16" : "uint32").Append('"');
        else
            json.Append("null");
    }

    private static void AppendDepthState(StringBuilder json, TextureFormat? format, bool depthWriteEnabled, CompareFunction depthCompare)
    {
        json.Append(",\"depth\":");
        if (format is not { } depthFormat)
        {
            json.Append("null");
            return;
        }
        json.Append("{\"format\":\"").Append(FormatName(depthFormat))
            .Append("\",\"write\":").Append(depthWriteEnabled ? "true" : "false")
            .Append(",\"compare\":\"").Append(CompareName(depthCompare)).Append("\"}");
    }

    private static void AppendVertexLayouts(StringBuilder json, ReadOnlySpan<VertexBufferLayoutDesc> layouts)
    {
        json.Append(",\"vertexLayouts\":[");
        for (var i = 0; i < layouts.Length; i++)
        {
            var layout = layouts[i];
            if (i > 0) json.Append(',');
            json.Append("{\"stride\":").Append(layout.Stride)
                .Append(",\"stepMode\":\"").Append(layout.StepMode == VertexStepMode.Instance ? "instance" : "vertex")
                .Append("\",\"attributes\":[");
            for (var a = 0; a < layout.Attributes.Length; a++)
            {
                var attribute = layout.Attributes[a];
                if (a > 0) json.Append(',');
                json.Append("{\"shaderLocation\":").Append(attribute.ShaderLocation)
                    .Append(",\"format\":\"").Append(VertexFormatName(attribute.Format))
                    .Append("\",\"offset\":").Append(attribute.Offset).Append('}');
            }
            json.Append("]}");
        }
        json.Append(']');
    }

    /// <summary>Serialize the program's bind-group layouts as a DENSE array indexed by group
    /// number — WebGPU requires no gaps, so a program that declares groups 0 and 2 gets an empty
    /// layout at 1. A program with no bindings at all emits null, which leaves the pipeline on
    /// WebGPU's implicit ("auto") layout.</summary>
    private static void AppendPipelineLayout(StringBuilder json, PipelineLayoutDesc? layout)
    {
        json.Append(",\"groups\":");
        if (layout is null || (layout.Groups.Length == 0 && layout.PushConstants.Length == 0))
        {
            json.Append("null");
            return;
        }
        if (layout.PushConstants.Length > 0)
            throw new NotSupportedException("Push constants are not supported by the browser backend.");

        uint maxGroup = 0;
        foreach (var group in layout.Groups) maxGroup = Math.Max(maxGroup, group.GroupIndex);

        json.Append('[');
        for (var i = 0u; i <= maxGroup; i++)
        {
            if (i > 0) json.Append(',');
            BindGroupLayoutDesc? match = null;
            foreach (var group in layout.Groups)
            {
                if (group.GroupIndex != i) continue;
                match = group;
                break;
            }
            if (match is null) json.Append("[]");
            else AppendGroupLayout(json, match);
        }
        json.Append(']');
    }
}
