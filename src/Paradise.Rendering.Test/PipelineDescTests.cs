using System;

namespace Paradise.Rendering.Test;

public class PipelineDescTests
{
    private static PipelineDesc BuildSample(string? name = null, ShaderHandle? overrideVs = null)
    {
        var attrs = new[]
        {
            new VertexAttributeDesc(0, VertexFormat.Float32x2, 0),
            new VertexAttributeDesc(1, VertexFormat.Float32x3, 8),
        };
        var layouts = new[]
        {
            new VertexBufferLayoutDesc(20, VertexStepMode.Vertex, attrs),
        };
        return new PipelineDesc
        {
            Name = name,
            VertexShader = overrideVs ?? new ShaderHandle(1, 1),
            VertexEntryPoint = "vs_main",
            FragmentShader = new ShaderHandle(2, 1),
            FragmentEntryPoint = "fs_main",
            VertexLayouts = layouts,
            Topology = PrimitiveTopology.TriangleList,
            StripIndexFormat = IndexFormat.Uint16,
            ColorFormat = TextureFormat.Bgra8Unorm,
            DepthStencilFormat = null,
            Layout = null,
        };
    }

    [Test]
    public async Task content_hash_is_stable_for_equal_descriptors()
    {
        var a = BuildSample("a");
        var b = BuildSample("b"); // different name, same content
        await Assert.That(a.ContentHash()).IsEqualTo(b.ContentHash());
        await Assert.That(a).IsEqualTo(b);
    }

    [Test]
    public async Task content_hash_differs_when_shader_handle_differs()
    {
        var a = BuildSample();
        var b = BuildSample(overrideVs: new ShaderHandle(99, 1));
        await Assert.That(a.ContentHash()).IsNotEqualTo(b.ContentHash());
        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task content_hash_differs_when_vertex_layout_differs()
    {
        var a = BuildSample();
        var bAttrs = new[]
        {
            new VertexAttributeDesc(0, VertexFormat.Float32x4, 0),
        };
        var b = a with { }; // record-struct-style copy isn't supported on plain struct, rebuild
        var bLayouts = new[] { new VertexBufferLayoutDesc(16, VertexStepMode.Vertex, bAttrs) };
        b = new PipelineDesc
        {
            Name = a.Name,
            VertexShader = a.VertexShader,
            VertexEntryPoint = a.VertexEntryPoint,
            FragmentShader = a.FragmentShader,
            FragmentEntryPoint = a.FragmentEntryPoint,
            VertexLayouts = bLayouts,
            Topology = a.Topology,
            StripIndexFormat = a.StripIndexFormat,
            ColorFormat = a.ColorFormat,
            DepthStencilFormat = a.DepthStencilFormat,
            Layout = a.Layout,
        };
        await Assert.That(a.ContentHash()).IsNotEqualTo(b.ContentHash());
    }

    [Test]
    public async Task name_does_not_participate_in_hash_or_equality()
    {
        var a = BuildSample("debug-1");
        var b = BuildSample("debug-2");
        await Assert.That(a == b).IsTrue();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }
}
