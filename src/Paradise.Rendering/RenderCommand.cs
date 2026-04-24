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

/// <summary>Payload for <see cref="RenderCommandKind.SetBindGroup"/>. The bind group itself is
/// referenced by handle; dynamic offsets (if any) live in the command stream's side
/// <see cref="RenderCommandStream.DynamicOffsets"/> buffer starting at
/// <see cref="DynamicOffsetsStart"/>, running for <see cref="DynamicOffsetsCount"/> entries.
/// <see cref="DynamicOffsetsCount"/> of <c>0</c> means "no dynamic offsets".</summary>
public readonly record struct SetBindGroupPayload(
    uint GroupIndex,
    BindGroupHandle BindGroup,
    uint DynamicOffsetsStart,
    uint DynamicOffsetsCount);

/// <summary>Discriminated render command. Kind selects one of the payload fields below; reading
/// any other field is undefined. Encode via <see cref="RenderCommandEncoder"/>.</summary>
/// <remarks>The struct uses an explicit 8-byte aligned layout: 1 byte kind + 7 bytes padding +
/// up to 40 bytes of payload (largest is <see cref="SetVertexBuffer"/> at 4+4-pad+16+8+8 = 40
/// bytes; <c>BufferHandle</c> is <c>Size = 16</c> via <see cref="StructLayoutAttribute"/>). The
/// declared <c>Size = 48</c> matches what the CLR computes for the field extents, so
/// <c>Unsafe.SizeOf&lt;RenderCommand&gt;()</c> returns 48 — verified by
/// <c>RenderCommandLayoutTests</c>. Sequential layout keeps the encoded stream a flat array of
/// 48-byte cells with no pointer indirection, preserving zero-allocation encoding.</remarks>
[StructLayout(LayoutKind.Explicit, Size = 48)]
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

    public static RenderCommand FromSetBindGroup(uint groupIndex, BindGroupHandle bindGroup, uint dynamicOffsetsStart, uint dynamicOffsetsCount) =>
        new(RenderCommandKind.SetBindGroup, new SetBindGroupPayload(groupIndex, bindGroup, dynamicOffsetsStart, dynamicOffsetsCount));

    public static RenderCommand FromDraw(in DrawCommand cmd) =>
        new(RenderCommandKind.Draw, cmd);

    public static RenderCommand FromDrawIndexed(in DrawIndexedCommand cmd) =>
        new(RenderCommandKind.DrawIndexed, cmd);
}

/// <summary>Append-only sequence of <see cref="RenderCommand"/>s plus side tables for payloads that
/// don't fit in the fixed 48-byte command struct: <see cref="Passes"/> holds
/// <see cref="RenderPassDesc"/> records referenced by <see cref="RenderCommandKind.BeginPass"/>, and
/// <see cref="DynamicOffsets"/> holds the <c>uint[]</c> ranges referenced by
/// <see cref="RenderCommandKind.SetBindGroup"/>.</summary>
public readonly struct RenderCommandStream
{
    public ReadOnlyMemory<RenderCommand> Commands { get; init; }

    /// <summary>Render passes referenced by <see cref="RenderCommandKind.BeginPass"/> via index.
    /// Pass descriptors hold inline color attachment storage and live separately so the command
    /// stream itself stays a flat list of small fixed-size commands.</summary>
    public ReadOnlyMemory<RenderPassDesc> Passes { get; init; }

    /// <summary>Dynamic bind-group offsets referenced by <see cref="RenderCommandKind.SetBindGroup"/>
    /// via the payload's <see cref="SetBindGroupPayload.DynamicOffsetsStart"/> +
    /// <see cref="SetBindGroupPayload.DynamicOffsetsCount"/> range. Empty when no dynamic offsets are
    /// in use.</summary>
    public ReadOnlyMemory<uint> DynamicOffsets { get; init; }

    public RenderCommandStream(ReadOnlyMemory<RenderCommand> commands, ReadOnlyMemory<RenderPassDesc> passes)
        : this(commands, passes, ReadOnlyMemory<uint>.Empty)
    {
    }

    public RenderCommandStream(ReadOnlyMemory<RenderCommand> commands, ReadOnlyMemory<RenderPassDesc> passes, ReadOnlyMemory<uint> dynamicOffsets)
    {
        Commands = commands;
        Passes = passes;
        DynamicOffsets = dynamicOffsets;
    }
}
