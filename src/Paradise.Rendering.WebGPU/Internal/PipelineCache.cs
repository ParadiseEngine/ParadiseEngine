using WgRenderPipeline = WebGpuSharp.RenderPipeline;
using WgShaderModule = WebGpuSharp.ShaderModule;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Shares native pipelines and their dependencies while public pipeline handles remain live.</summary>
internal sealed class PipelineCache
{
    internal readonly record struct Key(PipelineDesc Description, WgShaderModule Vertex, WgShaderModule? Fragment);

    private readonly RefCountedCache<Key, NativeResource<WgRenderPipeline>> _entries = new(static resource => resource.Dispose());

    public RefCountedCache<Key, NativeResource<WgRenderPipeline>>.Lease Acquire(
        in PipelineDesc desc, WgShaderModule vertex, WgShaderModule? fragment,
        Func<NativeResource<WgRenderPipeline>> factory)
    {
        // Public shader handles can differ while their cached native modules are identical.
        var key = new Key(desc with { VertexShader = default, FragmentShader = default }, vertex, fragment);
        return _entries.Acquire(key, factory);
    }

    public int Count => _entries.Count;
    public void Clear() => _entries.Clear();
}
