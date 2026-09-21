using System.Buffers;

namespace Paradise.Rendering.WebGPU.Test;

public class ShaderConstantGpuTests
{
    private const string Source = """
        @id(0) override red: f32 = 0.0;
        @vertex fn vertexMain(@builtin(vertex_index) id: u32) -> @builtin(position) vec4<f32> {
            let xy = vec2<f32>(f32((id << 1u) & 2u), f32(id & 2u));
            return vec4<f32>(xy * 2.0 - 1.0, 0.0, 1.0);
        }
        @fragment fn fragmentMain() -> @location(0) vec4<f32> { return vec4<f32>(red, 0.0, 1.0 - red, 1.0); }
        """;

    [Test]
    public async Task DifferentOverridesOfOneModuleProduceDifferentPixelsAndSurvivePeerRelease()
    {
        WebGpuRenderer backend;
        try { backend = WebGpuRenderer.CreateHeadless(8, 8); }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No native WebGPU adapter: {error.Message}");
            return;
        }
        using var ownedBackend = backend;
        ShaderProgramDesc Program(double red) => new(
            [new ShaderModuleDesc(Source, "vertexMain", ShaderStage.Vertex),
             new ShaderModuleDesc(Source, "fragmentMain", ShaderStage.Fragment)
             { Constants = new ShaderConstant[] { new("0", red) } }], new PipelineLayoutDesc([], []), []);
        var redPipeline = backend.CreatePipeline(Program(1), backend.ColorFormat);
        var bluePipeline = backend.CreatePipeline(Program(0), backend.ColorFormat);
        byte[] Render(PipelineHandle pipeline)
        {
            var pass = new RenderPassDesc(1);
            pass.Colors.Slot0 = new ColorAttachmentDesc(RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, ColorRgba.Black);
            var writer = new ArrayBufferWriter<RenderCommand>();
            var encoder = new RenderCommandEncoder(writer);
            encoder.BeginPass(0);
            encoder.SetPipeline(pipeline);
            encoder.Draw(new DrawCommand(3, 1, 0, 0));
            encoder.EndPass();
            var stream = new RenderCommandStream(writer.WrittenMemory, new[] { pass });
            backend.Submit(stream);
            return backend.ReadbackColor(out _, out _);
        }
        var redPixels = Render(redPipeline);
        var bluePixels = Render(bluePipeline);
        await Assert.That(redPixels.SequenceEqual(bluePixels)).IsFalse();
        await Assert.That(redPixels[3]).IsEqualTo((byte)255);
        backend.DestroyPipeline(redPipeline);
        await Assert.That(Render(bluePipeline).SequenceEqual(bluePixels)).IsTrue();
        backend.DestroyPipeline(bluePipeline);
    }
}
