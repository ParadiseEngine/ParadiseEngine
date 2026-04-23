using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.Test;

public class PipelineCacheTests
{
    private static PipelineDesc MakeDesc(string? name = null, ShaderHandle? overrideVs = null)
    {
        var attrs = new[] { new VertexAttributeDesc(0, VertexFormat.Float32x2, 0) };
        var layouts = new[] { new VertexBufferLayoutDesc(8, VertexStepMode.Vertex, attrs) };
        return new PipelineDesc
        {
            Name = name,
            VertexShader = overrideVs ?? new ShaderHandle(1, 1),
            VertexEntryPoint = "vs",
            FragmentShader = new ShaderHandle(2, 1),
            FragmentEntryPoint = "fs",
            VertexLayouts = layouts,
            Topology = PrimitiveTopology.TriangleList,
            ColorFormat = TextureFormat.Bgra8Unorm,
        };
    }

    [Test]
    public async Task identical_desc_returns_cached_handle()
    {
        var cache = new PipelineCache();
        var calls = 0;
        var d1 = MakeDesc("a");
        var d2 = MakeDesc("b"); // different name, same content
        var h1 = cache.GetOrCreate(in d1, _ => { calls++; return new PipelineHandle(7, 1); });
        var h2 = cache.GetOrCreate(in d2, _ => { calls++; return new PipelineHandle(99, 1); });
        await Assert.That(h1).IsEqualTo(h2);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task differing_desc_creates_distinct_handle()
    {
        var cache = new PipelineCache();
        var calls = 0;
        var d1 = MakeDesc(overrideVs: new ShaderHandle(1, 1));
        var d2 = MakeDesc(overrideVs: new ShaderHandle(2, 1));
        var h1 = cache.GetOrCreate(in d1, _ => { calls++; return new PipelineHandle(10, 1); });
        var h2 = cache.GetOrCreate(in d2, _ => { calls++; return new PipelineHandle(11, 1); });
        await Assert.That(h1).IsNotEqualTo(h2);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task forget_removes_cache_entry()
    {
        var cache = new PipelineCache();
        var d = MakeDesc();
        var nextHandle = 100u;
        var h = cache.GetOrCreate(in d, _ => new PipelineHandle(nextHandle++, 1));
        cache.Forget(h);
        await Assert.That(cache.TryGet(in d, out _)).IsFalse();
        var h2 = cache.GetOrCreate(in d, _ => new PipelineHandle(nextHandle++, 1));
        await Assert.That(h2).IsNotEqualTo(h);
    }
}
