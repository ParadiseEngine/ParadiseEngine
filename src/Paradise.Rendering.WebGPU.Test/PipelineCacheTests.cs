using System;
using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Checks the native pipeline cache beneath public resource handles.</summary>
/// <remarks>Creating native pipelines requires a GPU. HandleDistinctnessTests cover independent
/// public handles sharing a cached native pipeline.</remarks>
public class PipelineCacheTests
{
    [Test]
    public async Task cache_starts_empty_and_clear_resets_state()
    {
        var cache = new PipelineCache();
        await Assert.That(cache.Count).IsEqualTo(0);
        cache.Clear();
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task try_get_returns_false_for_uncached_desc()
    {
        var cache = new PipelineCache();
        var attrs = new[] { new VertexAttributeDesc(0, VertexFormat.Float32x2, 0) };
        var layouts = new[] { new VertexBufferLayoutDesc(8, VertexStepMode.Vertex, attrs) };
        var desc = new PipelineDesc
        {
            VertexShader = new ShaderHandle(1, 1),
            VertexEntryPoint = "vs",
            FragmentShader = new ShaderHandle(2, 1),
            FragmentEntryPoint = "fs",
            VertexLayouts = layouts,
            Topology = PrimitiveTopology.TriangleList,
            ColorFormat = TextureFormat.Bgra8Unorm,
        };
        await Assert.That(cache.TryGetNative(in desc, out _)).IsFalse();
    }
}
