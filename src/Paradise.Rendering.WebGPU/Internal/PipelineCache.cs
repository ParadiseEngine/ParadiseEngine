using Paradise.Rendering.Internal;
using WgRenderPipeline = WebGpuSharp.RenderPipeline;
using WgShaderModule = WebGpuSharp.ShaderModule;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Shares native pipelines and their dependencies while public pipeline handles remain live.</summary>
internal sealed class PipelineCache
{
    internal readonly record struct Key
    {
        private readonly PipelineDesc _description;
        private readonly WgShaderModule _vertex;
        private readonly WgShaderModule? _fragment;

        internal Key(in PipelineDesc description, WgShaderModule vertex, WgShaderModule? fragment)
        {
            // Descriptor records contain caller-owned arrays; the dictionary must own its key's content.
            var vertexLayouts = description.VertexLayouts.ToArray();
            for (var i = 0; i < vertexLayouts.Length; i++)
            {
                var layout = vertexLayouts[i];
                vertexLayouts[i] = layout with { Attributes = layout.Attributes.AsSpan().ToArray() };
            }

            PipelineLayoutDesc? pipelineLayout = null;
            if (description.Layout is { } sourceLayout)
            {
                var groups = sourceLayout.Groups.AsSpan().ToArray();
                for (var i = 0; i < groups.Length; i++)
                {
                    var group = groups[i];
                    groups[i] = group with { Entries = group.Entries.AsSpan().ToArray() };
                }
                pipelineLayout = sourceLayout with
                {
                    Groups = groups,
                    PushConstants = sourceLayout.PushConstants.AsSpan().ToArray(),
                };
            }

            // Public shader handles can differ while their cached native modules are identical.
            _description = description with
            {
                VertexShader = default,
                FragmentShader = default,
                VertexLayouts = vertexLayouts,
                Layout = pipelineLayout,
            };
            _vertex = vertex;
            _fragment = fragment;
        }
    }

    private readonly RefCountedCache<Key, NativeResource<WgRenderPipeline>> _entries = new(static resource => resource.Dispose());

    public RefCountedCache<Key, NativeResource<WgRenderPipeline>>.Lease Acquire(
        in PipelineDesc desc, WgShaderModule vertex, WgShaderModule? fragment,
        Func<NativeResource<WgRenderPipeline>> factory)
    {
        var key = new Key(desc, vertex, fragment);
        return _entries.Acquire(key, factory);
    }

    public int Count => _entries.Count;
    public void Clear() => _entries.Clear();
}
