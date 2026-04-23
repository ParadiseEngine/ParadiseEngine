using System.Runtime.CompilerServices;

namespace Paradise.Rendering.Test;

/// <summary>Locks the actual size of <see cref="RenderCommand"/> against its declared
/// <c>StructLayoutAttribute.Size</c>. The CLR silently extends an explicit-layout struct when
/// declared <c>Size</c> is smaller than the field extents — declaring 40 while
/// <see cref="SetVertexBufferPayload"/> at <c>FieldOffset(8)</c> needs 48 bytes (4 slot + 4 pad
/// + 16 BufferHandle + 8 offset + 8 size) silently rounds up and produces no diagnostic. This
/// test catches the mismatch so future payload additions surface size drift immediately rather
/// than degrading the declared cap into a polite suggestion.</summary>
public class RenderCommandLayoutTests
{
    [Test]
    public async Task render_command_size_matches_declared_layout()
    {
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
