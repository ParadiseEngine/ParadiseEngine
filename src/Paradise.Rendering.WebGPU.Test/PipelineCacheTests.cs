using System;
using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>The native-pipeline <see cref="PipelineCache"/> sits below the public handle layer
/// (iteration 3 restructure): <c>GetOrCreateNative</c> returns a cached <c>WgRenderPipeline</c>
/// reference; the public <see cref="PipelineHandle"/> minting happens in
/// <see cref="WebGpuRenderer"/> via <c>WebGpuDevice.RegisterPipeline</c>. The cache type
/// parameterizes on <c>WebGpuSharp.RenderPipeline</c> which has no public constructor — directly
/// unit-testing the cache requires a live device. The handle-distinctness invariants the cache
/// underpins are covered by <see cref="HandleDistinctnessTests"/> through the public renderer
/// surface (skipped when no GPU available).</summary>
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
