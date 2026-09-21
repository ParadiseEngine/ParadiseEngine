using System;
using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Checks the ownership cache used beneath native resource handles without a GPU.</summary>
public class PipelineCacheTests
{
    [Test]
    [Arguments("vertex buffers")]
    [Arguments("vertex attributes")]
    [Arguments("bind groups")]
    [Arguments("bind group entries")]
    [Arguments("push constants")]
    [Arguments("vertex constants")]
    [Arguments("fragment constants")]
    public async Task caller_descriptor_mutation_cannot_strand_or_reuse_a_retired_pipeline(string mutation)
    {
        var attribute = new VertexAttributeDesc(0, VertexFormat.Float32x3, 0);
        VertexAttributeDesc[] attributes = [attribute];
        var vertexBuffer = new VertexBufferLayoutDesc(12, VertexStepMode.Vertex, attributes);
        VertexBufferLayoutDesc[] vertexBuffers = [vertexBuffer];
        var binding = new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer);
        BindGroupLayoutEntryDesc[] bindings = [binding];
        var group = new BindGroupLayoutDesc(0, bindings);
        BindGroupLayoutDesc[] groups = [group];
        var pushConstant = new PushConstantRangeDesc(ShaderStage.Vertex, 0, 16);
        PushConstantRangeDesc[] pushConstants = [pushConstant];
        ShaderConstant[] vertexConstants = [new("0", 1)];
        ShaderConstant[] fragmentConstants = [new("0", 2)];
        var descriptor = new PipelineDesc
        {
            VertexLayouts = vertexBuffers,
            VertexConstants = vertexConstants,
            FragmentConstants = fragmentConstants,
            Layout = new PipelineLayoutDesc(groups, pushConstants),
        };
        var creations = 0;
        var cache = new PipelineCache();
        NativeResource<WebGpuSharp.RenderPipeline> Create()
        {
            creations++;
            // Cache ownership needs no native calls; stand-ins keep this regression independent of a GPU.
            return new NativeResource<WebGpuSharp.RenderPipeline>(null!, []);
        }
        using var first = cache.Acquire(descriptor, null!, null, Create);
        using var duplicate = cache.Acquire(descriptor with
        {
            Name = "Same native pipeline",
            VertexShader = new ShaderHandle(7, 1),
            FragmentShader = new ShaderHandle(8, 1),
        }, null!, null, static () => throw new InvalidOperationException("Rebuilt live pipeline."));
        await Assert.That(ReferenceEquals(first.Value, duplicate.Value)).IsTrue();

        switch (mutation)
        {
            case "vertex buffers": vertexBuffers[0] = vertexBuffer with { Stride = 24 }; break;
            case "vertex attributes": attributes[0] = attribute with { Offset = 4 }; break;
            case "bind groups": groups[0] = group with { GroupIndex = 1 }; break;
            case "bind group entries": bindings[0] = binding with { Binding = 1 }; break;
            case "push constants": pushConstants[0] = pushConstant with { Size = 32 }; break;
            case "vertex constants": vertexConstants[0] = new("0", 3); break;
            case "fragment constants": fragmentConstants[0] = new("0", 4); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        first.Dispose();
        await Assert.That(cache.Count).IsEqualTo(1);
        duplicate.Dispose();
        await Assert.That(cache.Count).IsEqualTo(0);
        vertexConstants[0] = new("0", 1);
        fragmentConstants[0] = new("0", 2);
        vertexBuffers[0] = vertexBuffer;
        attributes[0] = attribute;
        groups[0] = group;
        bindings[0] = binding;
        pushConstants[0] = pushConstant;

        using var replacement = cache.Acquire(descriptor, null!, null, Create);
        await Assert.That(creations).IsEqualTo(2);
        await Assert.That(replacement.Value).IsNotNull();
        await Assert.That(cache.Count).IsEqualTo(1);
        replacement.Dispose();
        await Assert.That(cache.Count).IsEqualTo(0);
    }
}
