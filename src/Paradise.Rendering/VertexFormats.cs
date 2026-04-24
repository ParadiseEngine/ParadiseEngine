using System;

namespace Paradise.Rendering;

/// <summary>Static helpers for the <see cref="VertexFormat"/> enum — byte size of one element.</summary>
public static class VertexFormats
{
    /// <summary>Size in bytes of one element of <paramref name="format"/>. Throws for
    /// <see cref="VertexFormat.Undefined"/>.</summary>
    public static ulong ByteSize(VertexFormat format) => format switch
    {
        VertexFormat.Float32 => 4,
        VertexFormat.Float32x2 => 8,
        VertexFormat.Float32x3 => 12,
        VertexFormat.Float32x4 => 16,
        VertexFormat.Uint8x4 => 4,
        VertexFormat.Unorm8x4 => 4,
        VertexFormat.Sint16x2 => 4,
        VertexFormat.Snorm16x2 => 4,
        VertexFormat.Uint16x2 => 4,
        VertexFormat.Uint16x4 => 8,
        VertexFormat.Sint32 => 4,
        VertexFormat.Sint32x2 => 8,
        VertexFormat.Sint32x3 => 12,
        VertexFormat.Sint32x4 => 16,
        VertexFormat.Uint32 => 4,
        VertexFormat.Uint32x2 => 8,
        VertexFormat.Uint32x3 => 12,
        VertexFormat.Uint32x4 => 16,
        _ => throw new ArgumentException($"Unknown vertex format '{format}'.", nameof(format)),
    };
}
