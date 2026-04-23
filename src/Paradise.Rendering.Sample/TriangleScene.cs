using System;
using System.Buffers;
using System.Reflection;
using Paradise.Rendering;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Sample;

/// <summary>One-frame draw plumbing for the M1 triangle. Loads the embedded Slang outputs, builds a
/// pipeline whose vertex layout comes from the reflection record (NOT hand-coded), uploads the
/// vertex buffer once, and produces a <see cref="RenderCommandStream"/> per frame.</summary>
internal sealed class TriangleScene : IDisposable
{
    // Three positions + three colors interleaved (xy, rgb): centered triangle, primary colors at
    // each corner. Layout MUST match what triangle.slang declares for VsIn — the engine asserts
    // this in the reflection round-trip test, so any drift here would surface immediately.
    private static readonly float[] s_vertices =
    {
        // pos.x, pos.y, color.r, color.g, color.b
         0.0f,  0.6f,   1.0f, 0.0f, 0.0f,
        -0.6f, -0.6f,   0.0f, 1.0f, 0.0f,
         0.6f, -0.6f,   0.0f, 0.0f, 1.0f,
    };

    private readonly WebGpuRenderer _renderer;
    private readonly BufferHandle _vertexBuffer;
    private readonly PipelineHandle _pipeline;
    private readonly RenderPassDesc[] _passes;
    // Reused per-frame so the sample's render loop is allocation-free at steady state. Cleared
    // at the top of RenderFrame; capacity grows once and stays put across frames.
    private readonly ArrayBufferWriter<RenderCommand> _commandWriter = new(8);

    public TriangleScene(WebGpuRenderer renderer)
    {
        _renderer = renderer;

        var program = WebGpuRenderer.LoadShaderProgram(typeof(TriangleScene).Assembly, "Shaders.triangle");
        _pipeline = renderer.CreatePipeline(program, renderer.ColorFormat);

        var bufferDesc = new BufferDesc(
            Name: "TriangleVertices",
            Size: (ulong)(s_vertices.Length * sizeof(float)),
            Usage: BufferUsage.Vertex,
            MappedAtCreation: false);
        _vertexBuffer = renderer.CreateBufferWithData(in bufferDesc, (ReadOnlySpan<float>)s_vertices);

        _passes = new RenderPassDesc[1];
        _passes[0] = new RenderPassDesc(colorAttachmentCount: 1);
        _passes[0].Colors.Slot0 = new ColorAttachmentDesc(
            View: RenderViewHandle.Invalid,  // backbuffer — see WebGpuRenderer.BeginPass
            Load: LoadOp.Clear,
            Store: StoreOp.Store,
            ClearValue: ColorRgba.CornflowerBlue);
    }

    public void RenderFrame()
    {
        _commandWriter.ResetWrittenCount();
        var encoder = new RenderCommandEncoder(_commandWriter);
        encoder.BeginPass(0);
        encoder.SetPipeline(_pipeline);
        encoder.SetVertexBuffer(0, _vertexBuffer, 0, (ulong)(s_vertices.Length * sizeof(float)));
        encoder.Draw(new DrawCommand(VertexCount: 3, InstanceCount: 1, FirstVertex: 0, FirstInstance: 0));
        encoder.EndPass();

        var stream = new RenderCommandStream(_commandWriter.WrittenMemory, _passes);
        _renderer.Submit(in stream);
    }

    public void Dispose()
    {
        _renderer.DestroyPipeline(_pipeline);
        _renderer.DestroyBuffer(_vertexBuffer);
    }
}
