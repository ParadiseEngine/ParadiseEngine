using System.Runtime.InteropServices;

namespace Paradise.Rendering;

/// <summary>Discriminator for <see cref="RenderCommand"/> — picks which payload field is live.</summary>
public enum RenderCommandKind : byte
{
    BeginPass = 0,
    EndPass,
    SetPipeline,
    SetVertexBuffer,
    SetIndexBuffer,
    SetBindGroup,
    Draw,
    DrawIndexed,
}

/// <summary>Payload for <see cref="RenderCommandKind.BeginPass"/>: index into
/// <see cref="RenderCommandStream.Passes"/>.</summary>
public readonly record struct BeginPassPayload(int PassIndex);

/// <summary>Payload for <see cref="RenderCommandKind.SetPipeline"/>.</summary>
public readonly record struct SetPipelinePayload(PipelineHandle Pipeline);

/// <summary>Payload for <see cref="RenderCommandKind.SetVertexBuffer"/>.</summary>
public readonly record struct SetVertexBufferPayload(uint Slot, BufferHandle Buffer, ulong Offset, ulong Size);

/// <summary>Payload for <see cref="RenderCommandKind.SetIndexBuffer"/>.</summary>
public readonly record struct SetIndexBufferPayload(BufferHandle Buffer, IndexFormat Format, ulong Offset, ulong Size);

/// <summary>Payload reserved for <see cref="RenderCommandKind.SetBindGroup"/>. M2 fills out the
/// resource binding shape; M1 emits the command but the backend no-ops it.</summary>
public readonly record struct SetBindGroupPayload(uint GroupIndex);

/// <summary>Discriminated render command. Kind selects one of the payload fields below; reading
/// any other field is undefined. Encode via <see cref="RenderCommandEncoder"/>.</summary>
/// <remarks>The struct uses an explicit 8-byte aligned layout: 1 byte kind + 7 bytes padding +
/// up to 32 bytes of payload (largest is <see cref="SetVertexBuffer"/>). Sequential layout makes
/// the discriminator trivially testable and keeps the encoded stream small without forcing a
/// pointer-indirection design that would compromise zero-allocation encoding.</remarks>
[StructLayout(LayoutKind.Explicit, Size = 40)]
public readonly struct RenderCommand
{
    [FieldOffset(0)] public readonly RenderCommandKind Kind;

    [FieldOffset(8)] public readonly BeginPassPayload BeginPass;
    [FieldOffset(8)] public readonly SetPipelinePayload SetPipeline;
    [FieldOffset(8)] public readonly SetVertexBufferPayload SetVertexBuffer;
    [FieldOffset(8)] public readonly SetIndexBufferPayload SetIndexBuffer;
    [FieldOffset(8)] public readonly SetBindGroupPayload SetBindGroup;
    [FieldOffset(8)] public readonly DrawCommand Draw;
    [FieldOffset(8)] public readonly DrawIndexedCommand DrawIndexed;

    private RenderCommand(RenderCommandKind kind, BeginPassPayload p) : this()
    {
        Kind = kind;
        BeginPass = p;
    }

    private RenderCommand(RenderCommandKind kind, SetPipelinePayload p) : this()
    {
        Kind = kind;
        SetPipeline = p;
    }

    private RenderCommand(RenderCommandKind kind, SetVertexBufferPayload p) : this()
    {
        Kind = kind;
        SetVertexBuffer = p;
    }

    private RenderCommand(RenderCommandKind kind, SetIndexBufferPayload p) : this()
    {
        Kind = kind;
        SetIndexBuffer = p;
    }

    private RenderCommand(RenderCommandKind kind, SetBindGroupPayload p) : this()
    {
        Kind = kind;
        SetBindGroup = p;
    }

    private RenderCommand(RenderCommandKind kind, DrawCommand p) : this()
    {
        Kind = kind;
        Draw = p;
    }

    private RenderCommand(RenderCommandKind kind, DrawIndexedCommand p) : this()
    {
        Kind = kind;
        DrawIndexed = p;
    }

    private RenderCommand(RenderCommandKind kind) : this()
    {
        Kind = kind;
    }

    public static RenderCommand FromBeginPass(int passIndex) =>
        new(RenderCommandKind.BeginPass, new BeginPassPayload(passIndex));

    public static RenderCommand FromEndPass() =>
        new(RenderCommandKind.EndPass);

    public static RenderCommand FromSetPipeline(PipelineHandle pipeline) =>
        new(RenderCommandKind.SetPipeline, new SetPipelinePayload(pipeline));

    public static RenderCommand FromSetVertexBuffer(uint slot, BufferHandle buffer, ulong offset, ulong size) =>
        new(RenderCommandKind.SetVertexBuffer, new SetVertexBufferPayload(slot, buffer, offset, size));

    public static RenderCommand FromSetIndexBuffer(BufferHandle buffer, IndexFormat format, ulong offset, ulong size) =>
        new(RenderCommandKind.SetIndexBuffer, new SetIndexBufferPayload(buffer, format, offset, size));

    public static RenderCommand FromSetBindGroup(uint groupIndex) =>
        new(RenderCommandKind.SetBindGroup, new SetBindGroupPayload(groupIndex));

    public static RenderCommand FromDraw(in DrawCommand cmd) =>
        new(RenderCommandKind.Draw, cmd);

    public static RenderCommand FromDrawIndexed(in DrawIndexedCommand cmd) =>
        new(RenderCommandKind.DrawIndexed, cmd);
}

/// <summary>Append-only sequence of <see cref="RenderCommand"/>s plus a side table of
/// <see cref="RenderPassDesc"/> records referenced by <see cref="RenderCommandKind.BeginPass"/>.</summary>
public readonly struct RenderCommandStream
{
    public ReadOnlyMemory<RenderCommand> Commands { get; init; }

    /// <summary>Render passes referenced by <see cref="RenderCommandKind.BeginPass"/> via index.
    /// Pass descriptors hold inline color attachment storage and live separately so the command
    /// stream itself stays a flat list of small fixed-size commands.</summary>
    public ReadOnlyMemory<RenderPassDesc> Passes { get; init; }

    public RenderCommandStream(ReadOnlyMemory<RenderCommand> commands, ReadOnlyMemory<RenderPassDesc> passes)
    {
        Commands = commands;
        Passes = passes;
    }
}
