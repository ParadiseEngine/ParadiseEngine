using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Rendering;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Sample;

/// <summary>The M2 demo: a rotating, checkerboard-textured, lambert-lit cube driven entirely by
/// the binding pipeline — a group-0 draw UBO (MVP + tint), a group-1 texture + sampler, and a
/// Depth32Float attachment. The pipeline layout and the UBO byte layout both come from slangc
/// reflection; the CPU-side <see cref="DrawParamsGpu"/> mirror is validated against the
/// reflected block at startup (the pattern the PBR renderer scales up later).</summary>
internal sealed class LitCubeScene : IDisposable
{
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    private struct DrawParamsGpu
    {
        [FieldOffset(0)] public Matrix4x4 Mvp; // raw System.Numerics bytes — WGSL's column-major
                                               // read of the row-major layout IS the transpose,
                                               // which matches mul(m, v) for numerics (v*M) math.
        [FieldOffset(64)] public Vector4 Tint;
    }

    // 24 vertices (4 per face — per-face normals), pos3 + normal3 + uv2 = 8 floats.
    private static readonly float[] s_vertices = BuildCubeVertices();
    private static readonly ushort[] s_indices = BuildCubeIndices();

    private readonly WebGpuRenderer _renderer;
    private readonly PipelineHandle _pipeline;
    private readonly BufferHandle _vertexBuffer;
    private readonly BufferHandle _indexBuffer;
    private readonly BufferHandle _drawUniformBuffer;
    private readonly TextureHandle _texture;
    private readonly SamplerHandle _sampler;
    private readonly BindGroupHandle _drawGroup;
    private readonly BindGroupHandle _materialGroup;
    private TextureHandle _depthTexture;
    private uint _depthWidth;
    private uint _depthHeight;
    private int _frame;
    private readonly ArrayBufferWriter<RenderCommand> _commandWriter = new(12);
    private readonly RenderPassDesc[] _passes = new RenderPassDesc[1];

    public LitCubeScene(WebGpuRenderer renderer, uint width, uint height)
    {
        _renderer = renderer;

        var program = WebGpuRenderer.LoadShaderProgram(typeof(LitCubeScene).Assembly, "Shaders.cube");

        // Reflection-validated uniforms: the CPU mirror must byte-match what slangc reflected —
        // total size AND every field's offset/size, so the [FieldOffset] constants above are
        // cross-checked rather than hand-trusted. This is the pattern the PBR renderer scales up.
        var drawBlock = program.UniformBlocks[0];
        if (drawBlock.SizeBytes != (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<DrawParamsGpu>())
            throw new InvalidOperationException(
                $"DrawParamsGpu is {System.Runtime.CompilerServices.Unsafe.SizeOf<DrawParamsGpu>()} bytes but the shader reflects {drawBlock.SizeBytes}.");
        ValidateField(drawBlock, "mvp", expectedOffset: 0, expectedSize: 64);
        ValidateField(drawBlock, "tint", expectedOffset: 64, expectedSize: 16);

        _pipeline = renderer.CreatePipeline(
            program, renderer.ColorFormat, depthStencilFormat: TextureFormat.Depth32Float);

        var vbDesc = new BufferDesc("CubeVertices", 0, BufferUsage.Vertex);
        _vertexBuffer = renderer.CreateBufferWithData(in vbDesc, (ReadOnlySpan<float>)s_vertices);
        var ibDesc = new BufferDesc("CubeIndices", 0, BufferUsage.Index);
        _indexBuffer = renderer.CreateBufferWithData(in ibDesc, (ReadOnlySpan<ushort>)s_indices);

        var uboDesc = new BufferDesc("CubeDrawParams", drawBlock.SizeBytes, BufferUsage.Uniform | BufferUsage.CopyDst);
        _drawUniformBuffer = renderer.CreateBuffer(in uboDesc);

        _texture = CreateCheckerboard(renderer);
        var samplerDesc = new SamplerDesc(
            "CubeSampler",
            SamplerAddressMode.Repeat, SamplerAddressMode.Repeat, SamplerAddressMode.Repeat,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest);
        _sampler = renderer.CreateSampler(in samplerDesc);

        var drawGroupDesc = new BindGroupDesc("CubeDrawGroup", program.Layout.Groups[0], new[]
        {
            BindGroupEntryDesc.ForBuffer(0, _drawUniformBuffer, 0, drawBlock.SizeBytes),
        });
        _drawGroup = renderer.CreateBindGroup(in drawGroupDesc);

        var materialGroupDesc = new BindGroupDesc("CubeMaterialGroup", program.Layout.Groups[1], new[]
        {
            BindGroupEntryDesc.ForTexture(0, _texture),
            BindGroupEntryDesc.ForSampler(1, _sampler),
        });
        _materialGroup = renderer.CreateBindGroup(in materialGroupDesc);

        _depthWidth = Math.Max(1, width);
        _depthHeight = Math.Max(1, height);
        _depthTexture = CreateDepthTexture(_depthWidth, _depthHeight);

        _passes[0] = new RenderPassDesc(colorAttachmentCount: 1)
        {
            Depth = new DepthAttachmentDesc(_depthTexture, LoadOp.Clear, StoreOp.Store, ClearDepth: 1f),
        };
        _passes[0].Colors.Slot0 = new ColorAttachmentDesc(
            View: RenderViewHandle.Invalid,
            Load: LoadOp.Clear,
            Store: StoreOp.Store,
            ClearValue: ColorRgba.CornflowerBlue);
    }

    /// <summary>The depth buffer must track the swapchain size; call on window resize.</summary>
    public void Resize(uint width, uint height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _depthWidth && height == _depthHeight) return;
        _renderer.DestroyTexture(_depthTexture);
        _depthWidth = width;
        _depthHeight = height;
        _depthTexture = CreateDepthTexture(width, height);
        _passes[0].Depth = new DepthAttachmentDesc(_depthTexture, LoadOp.Clear, StoreOp.Store, ClearDepth: 1f);
    }

    public void RenderFrame()
    {
        _frame++;
        var angle = _frame * (MathF.PI / 180f); // 1°/frame
        var world = Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateRotationX(angle * 0.37f);
        var view = Matrix4x4.CreateLookAt(new Vector3(0f, 1.2f, 2.6f), Vector3.Zero, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, _depthWidth / (float)_depthHeight, 0.1f, 100f);

        var uniforms = new DrawParamsGpu
        {
            Mvp = world * view * proj,
            Tint = new Vector4(1f, 1f, 1f, 1f),
        };
        _renderer.UpdateBuffer<DrawParamsGpu>(_drawUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        _commandWriter.ResetWrittenCount();
        var encoder = new RenderCommandEncoder(_commandWriter);
        encoder.BeginPass(0);
        encoder.SetPipeline(_pipeline);
        encoder.SetBindGroup(0, _drawGroup);
        encoder.SetBindGroup(1, _materialGroup);
        encoder.SetVertexBuffer(0, _vertexBuffer, 0, (ulong)(s_vertices.Length * sizeof(float)));
        encoder.SetIndexBuffer(_indexBuffer, IndexFormat.Uint16, 0, (ulong)(s_indices.Length * sizeof(ushort)));
        encoder.DrawIndexed(new DrawIndexedCommand((uint)s_indices.Length, 1, 0, 0, 0));
        encoder.EndPass();

        var stream = new RenderCommandStream(_commandWriter.WrittenMemory, _passes);
        _renderer.Submit(in stream);
    }

    private static void ValidateField(UniformBlockDesc block, string name, uint expectedOffset, uint expectedSize)
    {
        foreach (var field in block.Fields)
        {
            if (!string.Equals(field.Name, name, StringComparison.Ordinal)) continue;
            if (field.Offset != expectedOffset || field.Size != expectedSize)
                throw new InvalidOperationException(
                    $"Uniform field '{name}': CPU mirror expects offset {expectedOffset}/size {expectedSize} " +
                    $"but the shader reflects offset {field.Offset}/size {field.Size}.");
            return;
        }
        throw new InvalidOperationException($"Uniform block '{block.Name}' reflects no field named '{name}'.");
    }

    private TextureHandle CreateDepthTexture(uint width, uint height)
    {
        var desc = new TextureDesc(
            "CubeDepth", width, height, 1, 1, 1,
            TextureDimension.D2, TextureFormat.Depth32Float, TextureUsage.RenderAttachment);
        return _renderer.CreateTexture(in desc);
    }

    private static TextureHandle CreateCheckerboard(WebGpuRenderer renderer)
    {
        const uint Size = 8;
        var desc = new TextureDesc(
            "CubeCheckerboard", Size, Size, 1, 1, 1,
            TextureDimension.D2, TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst);
        var handle = renderer.CreateTexture(in desc);

        var pixels = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var i = (y * (int)Size + x) * 4;
                var light = ((x + y) & 1) == 0;
                pixels[i + 0] = light ? (byte)235 : (byte)90;
                pixels[i + 1] = light ? (byte)235 : (byte)120;
                pixels[i + 2] = light ? (byte)235 : (byte)200;
                pixels[i + 3] = 255;
            }
        }
        renderer.WriteTexture(handle, 0, pixels, bytesPerRow: Size * 4, rowsPerImage: Size, width: Size, height: Size);
        return handle;
    }

    private static float[] BuildCubeVertices()
    {
        // Six faces, 4 vertices each: pos3, normal3, uv2. Unit cube centered on the origin.
        var faces = new (Vector3 Normal, Vector3 U, Vector3 V)[]
        {
            (new(0, 0, 1), new(1, 0, 0), new(0, 1, 0)),   // +Z
            (new(0, 0, -1), new(-1, 0, 0), new(0, 1, 0)), // -Z
            (new(1, 0, 0), new(0, 0, -1), new(0, 1, 0)),  // +X
            (new(-1, 0, 0), new(0, 0, 1), new(0, 1, 0)),  // -X
            (new(0, 1, 0), new(1, 0, 0), new(0, 0, -1)),  // +Y
            (new(0, -1, 0), new(1, 0, 0), new(0, 0, 1)),  // -Y
        };

        var data = new float[faces.Length * 4 * 8];
        var w = 0;
        foreach (var (normal, u, v) in faces)
        {
            var center = normal * 0.5f;
            for (var corner = 0; corner < 4; corner++)
            {
                var su = corner is 1 or 2 ? 0.5f : -0.5f;
                var sv = corner is 2 or 3 ? 0.5f : -0.5f;
                var pos = center + u * su + v * sv;
                data[w++] = pos.X; data[w++] = pos.Y; data[w++] = pos.Z;
                data[w++] = normal.X; data[w++] = normal.Y; data[w++] = normal.Z;
                data[w++] = su + 0.5f; data[w++] = sv + 0.5f;
            }
        }
        return data;
    }

    private static ushort[] BuildCubeIndices()
    {
        var indices = new ushort[6 * 6];
        for (var face = 0; face < 6; face++)
        {
            var b = (ushort)(face * 4);
            var i = face * 6;
            indices[i + 0] = b;
            indices[i + 1] = (ushort)(b + 1);
            indices[i + 2] = (ushort)(b + 2);
            indices[i + 3] = b;
            indices[i + 4] = (ushort)(b + 2);
            indices[i + 5] = (ushort)(b + 3);
        }
        return indices;
    }

    public void Dispose()
    {
        _renderer.DestroyBindGroup(_materialGroup);
        _renderer.DestroyBindGroup(_drawGroup);
        _renderer.DestroySampler(_sampler);
        _renderer.DestroyTexture(_texture);
        _renderer.DestroyTexture(_depthTexture);
        _renderer.DestroyBuffer(_drawUniformBuffer);
        _renderer.DestroyBuffer(_indexBuffer);
        _renderer.DestroyBuffer(_vertexBuffer);
        _renderer.DestroyPipeline(_pipeline);
    }
}
