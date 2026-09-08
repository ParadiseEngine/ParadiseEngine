using System.Runtime.CompilerServices;

namespace Paradise.Rendering.Test;

/// <summary>Checks the actual RenderCommand size against its declared layout.</summary>
/// <remarks>The CLR silently expands explicit structs whose fields exceed Size; assert the 48-byte
/// layout so new payloads cannot change the command stride unnoticed.</remarks>
public class RenderCommandLayoutTests
{
    [Test]
    public async Task render_command_size_matches_declared_layout()
    {
        await Assert.That(Unsafe.SizeOf<RenderCommand>()).IsEqualTo(48);
    }

    [Test]
    public async Task opcode_values_are_the_wire_contract()
    {
        // The numeric kind values are decoded as opcodes by paradise-webgpu.js — pinning them
        // here makes an accidental reorder a test failure instead of a browser-only corruption.
        var expected = new Dictionary<RenderCommandKind, byte>
        {
            [RenderCommandKind.BeginPass] = 0,
            [RenderCommandKind.EndPass] = 1,
            [RenderCommandKind.SetPipeline] = 2,
            [RenderCommandKind.SetVertexBuffer] = 3,
            [RenderCommandKind.SetIndexBuffer] = 4,
            [RenderCommandKind.SetBindGroup] = 5,
            [RenderCommandKind.Draw] = 6,
            [RenderCommandKind.DrawIndexed] = 7,
            [RenderCommandKind.SetViewport] = 8,
            [RenderCommandKind.BeginComputePass] = 9,
            [RenderCommandKind.EndComputePass] = 10,
            [RenderCommandKind.SetComputePipeline] = 11,
            [RenderCommandKind.Dispatch] = 12,
        };
        foreach (var (kind, opcode) in expected)
        {
            await Assert.That((byte)kind).IsEqualTo(opcode);
        }
    }

    [Test]
    public async Task compute_payloads_fit_within_the_struct()
    {
        await Assert.That(Unsafe.SizeOf<SetComputePipelinePayload>()).IsEqualTo(16);
        await Assert.That(Unsafe.SizeOf<DispatchCommand>()).IsEqualTo(12);
        await Assert.That(Unsafe.SizeOf<RenderCommand>()).IsEqualTo(48);
    }

    [Test]
    public async Task largest_payload_set_vertex_buffer_fits_within_struct()
    {
        // SetVertexBufferPayload = (uint Slot, BufferHandle Buffer, ulong Offset, ulong Size)
        //   = 4 (Slot) + 4 (alignment pad) + 16 (BufferHandle is StructLayout.Size=16)
        //     + 8 (Offset) + 8 (Size) = 40 bytes
        // Placed at FieldOffset(8), the struct needs at least 48 bytes total.
        var payloadSize = Unsafe.SizeOf<SetVertexBufferPayload>();
        await Assert.That(payloadSize).IsEqualTo(40);
        await Assert.That(Unsafe.SizeOf<RenderCommand>()).IsGreaterThanOrEqualTo(payloadSize + 8);
    }
}
