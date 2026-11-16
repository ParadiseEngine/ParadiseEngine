using System.Buffers;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>A pipeline's cull mode reaches rasterization, with counter-clockwise front faces (#355).</summary>
public class CullModeGpuTests
{
    private const string Wgsl = """
        @vertex fn vsMain(@location(0) p : vec3<f32>) -> @builtin(position) vec4<f32> {
            return vec4<f32>(p, 1.0);
        }

        @fragment fn fsRed() -> @location(0) vec4<f32> {
            return vec4<f32>(1.0, 0.0, 0.0, 1.0);
        }
        """;

    // One triangle covering the viewport, counter-clockwise in clip space (y up): a front face.
    private static readonly float[] s_counterClockwise = [-1f, -1f, 0.5f, 3f, -1f, 0.5f, -1f, 3f, 0.5f];

    // The same triangle with two vertices swapped: clockwise, a back face.
    private static readonly float[] s_clockwise = [-1f, -1f, 0.5f, -1f, 3f, 0.5f, 3f, -1f, 0.5f];

    private static WebGpuRenderer? TryCreateHeadlessOrSkip()
    {
        try
        {
            // 64 texels make a 256-byte row, so a readback has no row padding to dilute the average.
            return WebGpuRenderer.CreateHeadless(64, 64);
        }
        catch (AdapterUnavailableException ex)
        {
            GpuRequirement.Skip($"No WebGPU adapter available on this host: {ex.Message}");
            return null;
        }
        catch (DllNotFoundException ex)
        {
            GpuRequirement.Skip($"WebGPU native library not loadable on this host: {ex.Message}");
            return null;
        }
    }

    private static bool Draws(WebGpuRenderer renderer, CullMode cull, float[] triangle)
    {
        var layout = new[]
        {
            new VertexBufferLayoutDesc(12, VertexStepMode.Vertex, [new VertexAttributeDesc(0, VertexFormat.Float32x3, 0)]),
        };
        var program = new ShaderProgramDesc(
            [new ShaderModuleDesc(Wgsl, "vsMain", ShaderStage.Vertex), new ShaderModuleDesc(Wgsl, "fsRed", ShaderStage.Fragment)],
            new PipelineLayoutDesc([], []),
            layout);
        var pipeline = renderer.CreatePipeline(program, renderer.ColorFormat, cullMode: cull);
        var vbDesc = new BufferDesc("cull-vb", 0, BufferUsage.Vertex);
        var vb = renderer.CreateBufferWithData(in vbDesc, (ReadOnlySpan<float>)triangle);
        try
        {
            var writer = new ArrayBufferWriter<RenderCommand>(8);
            var encoder = new RenderCommandEncoder(writer);
            encoder.BeginPass(0);
            encoder.SetPipeline(pipeline);
            encoder.SetVertexBuffer(0, vb, 0, (ulong)(triangle.Length * sizeof(float)));
            encoder.Draw(new DrawCommand(3, 1, 0, 0));
            encoder.EndPass();

            var passes = new RenderPassDesc[1];
            passes[0] = new RenderPassDesc(colorAttachmentCount: 1);
            passes[0].Colors.Slot0 = new ColorAttachmentDesc(RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, ColorRgba.Black);
            var stream = new RenderCommandStream(writer.WrittenMemory, passes);
            renderer.Submit(in stream);

            // Colour channels only: the opaque black clear puts 255 in every alpha byte (byte 3 in
            // both RGBA8 and BGRA8), which would read as "drawn" on an empty frame.
            var pixels = renderer.ReadbackColor(out _, out _);
            long sum = 0;
            for (var i = 0; i < pixels.Length; i += 4) sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
            return sum / (pixels.Length / 4 * 3.0) > 10.0;
        }
        finally
        {
            renderer.DestroyPipeline(pipeline);
            renderer.DestroyBuffer(vb);
        }
    }

    [Test]
    public async Task back_face_culling_drops_clockwise_triangles_only()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            await Assert.That(Draws(renderer, CullMode.None, s_counterClockwise)).IsTrue();
            await Assert.That(Draws(renderer, CullMode.None, s_clockwise)).IsTrue();
            await Assert.That(Draws(renderer, CullMode.Back, s_counterClockwise)).IsTrue();
            await Assert.That(Draws(renderer, CullMode.Back, s_clockwise)).IsFalse();
            await Assert.That(Draws(renderer, CullMode.Front, s_counterClockwise)).IsFalse();
            await Assert.That(Draws(renderer, CullMode.Front, s_clockwise)).IsTrue();
        }
        finally
        {
            renderer.Dispose();
        }
    }
}
