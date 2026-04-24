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

    [Test]
    public async Task layout_is_compared_structurally_not_by_reference()
    {
        // Regression: ShaderProgramLoader.BuildProgramDesc mints a fresh PipelineLayoutDesc per
        // load, so two loads of the same shader produce distinct Layout references. Reference-
        // equality Equals/ContentHash defeated the pipeline cache for that path — a cache miss
        // retained a duplicate native pipeline for the renderer's lifetime. Structural equality
        // (bind-group + push-constant deep walk) fixes the cache hit.
        var layoutA = new PipelineLayoutDesc(
            Groups: Array.Empty<BindGroupLayoutDesc>(),
            PushConstants: Array.Empty<PushConstantRangeDesc>());
        var layoutB = new PipelineLayoutDesc(
            Groups: Array.Empty<BindGroupLayoutDesc>(),
            PushConstants: Array.Empty<PushConstantRangeDesc>());
        await Assert.That(ReferenceEquals(layoutA, layoutB)).IsFalse();

        var a = BuildSample() with { };
        a = new PipelineDesc
        {
            VertexShader = new ShaderHandle(1, 1),
            VertexEntryPoint = "vs_main",
            FragmentShader = new ShaderHandle(2, 1),
            FragmentEntryPoint = "fs_main",
            VertexLayouts = new[] { new VertexBufferLayoutDesc(20, VertexStepMode.Vertex, new[] { new VertexAttributeDesc(0, VertexFormat.Float32x2, 0), new VertexAttributeDesc(1, VertexFormat.Float32x3, 8) }) },
            Topology = PrimitiveTopology.TriangleList,
            StripIndexFormat = IndexFormat.Uint16,
            ColorFormat = TextureFormat.Bgra8Unorm,
            DepthStencilFormat = null,
            Layout = layoutA,
        };
        var b = a with { Layout = layoutB };

        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a.ContentHash()).IsEqualTo(b.ContentHash());
    }

    [Test]
    public async Task layout_structural_inequality_is_detected()
    {
        var layoutEmpty = new PipelineLayoutDesc(
            Groups: Array.Empty<BindGroupLayoutDesc>(),
            PushConstants: Array.Empty<PushConstantRangeDesc>());
        var layoutWithBinding = new PipelineLayoutDesc(
            Groups: new[]
            {
                new BindGroupLayoutDesc(0, new[]
                {
                    new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16),
                }),
            },
            PushConstants: Array.Empty<PushConstantRangeDesc>());

        var a = BuildSample() with { Layout = layoutEmpty };
        var b = a with { Layout = layoutWithBinding };

        await Assert.That(a).IsNotEqualTo(b);
        await Assert.That(a.ContentHash()).IsNotEqualTo(b.ContentHash());
    }
}
