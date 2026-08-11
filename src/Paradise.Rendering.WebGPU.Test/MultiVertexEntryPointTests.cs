using System.Buffers;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>
/// A single WGSL module authoring TWO vertex entry points, drawing through each. Written while
/// bisecting the GPU-skinning black-frame: the PBR shader's rigid path renders and its skinned
/// twin silently draws nothing, with every layer above this one (reflection, layouts, pipeline
/// creation, command replay) verified correct. This strips the question to its minimum — if the
/// second-entry-point draw is black HERE, the fault lives in the WebGPU layer (or Dawn) and has
/// nothing to do with skinning.
/// </summary>
public class MultiVertexEntryPointTests
{
    // vsWide reads a second attribute the narrow entry point does not, mirroring the rigid/skinned
    // split: same module, different entry point, different vertex layout (stride 12 vs 28). The
    // fragment paints solid red so presence is unambiguous against a black clear.
    private const string Wgsl = """
        struct VOut { @builtin(position) pos : vec4<f32> };

        @vertex fn vsNarrow(@location(0) p : vec3<f32>) -> VOut {
            var o : VOut;
            o.pos = vec4<f32>(p, 1.0);
            return o;
        }

        @vertex fn vsWide(@location(0) p : vec3<f32>, @location(1) extra : vec4<f32>) -> VOut {
            var o : VOut;
            o.pos = vec4<f32>(p + vec3<f32>(extra.w - 1.0, 0.0, 0.0), 1.0);
            return o;
        }

        @fragment fn fsRed() -> @location(0) vec4<f32> {
            return vec4<f32>(1.0, 0.0, 0.0, 1.0);
        }
        """;

    private static WebGpuRenderer? TryCreateHeadlessOrSkip()
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(32, 32);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter available on this host: {ex.Message}");
            return null;
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native library not loadable on this host: {ex.Message}");
            return null;
        }
    }

    private static ShaderProgramDesc BuildProgram()
    {
        var narrowLayout = new[]
        {
            new VertexBufferLayoutDesc(12, VertexStepMode.Vertex,
                new[] { new VertexAttributeDesc(0, VertexFormat.Float32x3, 0) }),
        };
        var wideLayout = new[]
        {
            new VertexBufferLayoutDesc(28, VertexStepMode.Vertex,
                new[]
                {
                    new VertexAttributeDesc(0, VertexFormat.Float32x3, 0),
                    new VertexAttributeDesc(1, VertexFormat.Float32x4, 12),
                }),
        };
        return new ShaderProgramDesc(
            [
                new ShaderModuleDesc(Wgsl, "vsNarrow", ShaderStage.Vertex),
                new ShaderModuleDesc(Wgsl, "vsWide", ShaderStage.Vertex),
                new ShaderModuleDesc(Wgsl, "fsRed", ShaderStage.Fragment),
            ],
            new PipelineLayoutDesc([], []),
            narrowLayout)
        {
            VertexBuffersByEntryPoint = new Dictionary<string, VertexBufferLayoutDesc[]>(StringComparer.Ordinal)
            {
                ["vsNarrow"] = narrowLayout,
                ["vsWide"] = wideLayout,
            },
        };
    }

    private static double DrawAndMeasure(WebGpuRenderer renderer, string? vertexEntryPoint)
    {
        var program = BuildProgram();
        var pipeline = renderer.CreatePipeline(program, renderer.ColorFormat, vertexEntryPoint: vertexEntryPoint);

        var wide = vertexEntryPoint == "vsWide";
        // One large clip-space triangle covering the viewport centre. The wide layout appends a
        // vec4 whose w=1 makes vsWide's offset zero, so both entry points draw the same triangle.
        var stride = wide ? 7 : 3;
        var vertices = new float[3 * stride];
        ReadOnlySpan<float> positions = [-1f, -1f, 0.5f, 3f, -1f, 0.5f, -1f, 3f, 0.5f];
        for (var v = 0; v < 3; v++)
        {
            positions.Slice(v * 3, 3).CopyTo(vertices.AsSpan(v * stride));
            if (wide) vertices[v * stride + 6] = 1f; // extra.w = 1 → zero offset
        }
        var vbDesc = new BufferDesc("multi-entry-vb", 0, BufferUsage.Vertex);
        var vb = renderer.CreateBufferWithData(in vbDesc, (ReadOnlySpan<float>)vertices);

        var writer = new ArrayBufferWriter<RenderCommand>(8);
        var encoder = new RenderCommandEncoder(writer);
        encoder.BeginPass(0);
        encoder.SetPipeline(pipeline);
        encoder.SetVertexBuffer(0, vb, 0, (ulong)(vertices.Length * sizeof(float)));
        encoder.Draw(new DrawCommand(3, 1, 0, 0));
        encoder.EndPass();

        var passes = new RenderPassDesc[1];
        passes[0] = new RenderPassDesc(colorAttachmentCount: 1);
        passes[0].Colors.Slot0 = new ColorAttachmentDesc(RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, ColorRgba.Black);

        var stream = new RenderCommandStream(writer.WrittenMemory, passes);
        renderer.Submit(in stream);

        var pixels = renderer.ReadbackColor(out _, out _);
        long sum = 0;
        foreach (var b in pixels) sum += b;
        return sum / (double)pixels.Length;
    }

    [Test]
    public async Task first_entry_point_draws()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            await Assert.That(DrawAndMeasure(renderer, null)).IsGreaterThan(10.0);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task second_entry_point_draws()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            await Assert.That(DrawAndMeasure(renderer, "vsWide")).IsGreaterThan(10.0);
        }
        finally
        {
            renderer.Dispose();
        }
    }
}
