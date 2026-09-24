using System.Runtime.InteropServices;

namespace Paradise.Rendering;

/// <summary>Discriminator for <see cref="RenderCommand"/> — picks which payload field is live.</summary>
/// <remarks>The numeric values are a WIRE CONTRACT with the browser backend's frame decoder
/// (paradise-webgpu.js reads them as opcodes) — append new kinds at the end, never reorder.</remarks>
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
    SetViewport,
    // Compute passes carry no RenderPassDesc (no attachments), so BeginComputePass has no
    // pass-table index; EndComputePass is distinct from EndPass so pass-kind mismatches throw
    // eagerly; SetComputePipeline is distinct from SetPipeline because compute pipelines live
    // in their own handle space (see ComputePipelineHandle).
    BeginComputePass = 9,
    EndComputePass,
    SetComputePipeline,
    Dispatch,
    DrawIndexedIndirect,
    HostPass,
}

/// <summary>Payload for <see cref="RenderCommandKind.SetViewport"/>: the pixel-space viewport
/// rectangle (and depth range) the rasterizer maps NDC into — used to render each shadow-casting
/// light into its atlas tile. 24 bytes ≤ the 40-byte payload budget.</summary>
public readonly record struct SetViewportPayload(float X, float Y, float Width, float Height, float MinDepth, float MaxDepth);

/// <summary>Payload for <see cref="RenderCommandKind.BeginPass"/>: index into
/// <see cref="RenderCommandStream.Passes"/>.</summary>
public readonly record struct BeginPassPayload(int PassIndex);

/// <summary>Index into the stream's native callback table.</summary>
public readonly record struct HostPassPayload(int CallbackIndex);

/// <summary>Payload for <see cref="RenderCommandKind.SetPipeline"/>.</summary>
public readonly record struct SetPipelinePayload(PipelineHandle Pipeline);

/// <summary>Payload for <see cref="RenderCommandKind.SetVertexBuffer"/>.</summary>
public readonly record struct SetVertexBufferPayload(uint Slot, BufferHandle Buffer, ulong Offset, ulong Size);

/// <summary>Payload for <see cref="RenderCommandKind.SetIndexBuffer"/>.</summary>
public readonly record struct SetIndexBufferPayload(BufferHandle Buffer, IndexFormat Format, ulong Offset, ulong Size);

/// <summary>Payload for <see cref="RenderCommandKind.SetComputePipeline"/>.</summary>
public readonly record struct SetComputePipelinePayload(ComputePipelineHandle Pipeline);

/// <summary>Payload for <see cref="RenderCommandKind.SetBindGroup"/>: bind <paramref name="Group"/>
/// at <paramref name="GroupIndex"/>. When <paramref name="HasDynamicOffset"/> is set, the group's
/// dynamic-offset buffer entry is bound at <paramref name="DynamicOffset"/> bytes (layout must
/// have exactly one <c>HasDynamicOffset</c> entry). 4+16+4+1(+pad) = 28 bytes ≤ the 40-byte
/// payload budget.</summary>
public readonly record struct SetBindGroupPayload(uint GroupIndex, BindGroupHandle Group, uint DynamicOffset, bool HasDynamicOffset);

/// <summary>Stores a render command in an explicit 48-byte discriminated layout.</summary>
/// <remarks>Kind selects the valid payload; use RenderCommandEncoder. Eight-byte alignment leaves
/// 40 payload bytes, matching SetVertexBuffer and its 16-byte handle. Layout tests pin the
/// stride.</remarks>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public readonly struct RenderCommand
{
    [FieldOffset(0)] public readonly RenderCommandKind Kind;

    [FieldOffset(8)] public readonly BeginPassPayload BeginPass;
    [FieldOffset(8)] public readonly HostPassPayload HostPass;
    [FieldOffset(8)] public readonly SetPipelinePayload SetPipeline;
    [FieldOffset(8)] public readonly SetVertexBufferPayload SetVertexBuffer;
    [FieldOffset(8)] public readonly SetIndexBufferPayload SetIndexBuffer;
    [FieldOffset(8)] public readonly SetBindGroupPayload SetBindGroup;
    [FieldOffset(8)] public readonly DrawCommand Draw;
    [FieldOffset(8)] public readonly DrawIndexedCommand DrawIndexed;
    [FieldOffset(8)] public readonly SetViewportPayload SetViewport;
    [FieldOffset(8)] public readonly SetComputePipelinePayload SetComputePipeline;
    [FieldOffset(8)] public readonly DispatchCommand Dispatch;
    [FieldOffset(8)] public readonly DrawIndexedIndirectCommand DrawIndexedIndirect;

    private RenderCommand(RenderCommandKind kind, BeginPassPayload p) : this()
    {
        Kind = kind;
        BeginPass = p;
    }

    private RenderCommand(RenderCommandKind kind, HostPassPayload p) : this()
    {
        Kind = kind;
        HostPass = p;
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

    private RenderCommand(RenderCommandKind kind, SetViewportPayload p) : this()
    {
        Kind = kind;
        SetViewport = p;
    }

    private RenderCommand(RenderCommandKind kind, SetComputePipelinePayload p) : this()
    {
        Kind = kind;
        SetComputePipeline = p;
    }

    private RenderCommand(RenderCommandKind kind, DispatchCommand p) : this()
    {
        Kind = kind;
        Dispatch = p;
    }

    private RenderCommand(RenderCommandKind kind, DrawIndexedIndirectCommand p) : this()
    {
        Kind = kind;
        DrawIndexedIndirect = p;
    }

    private RenderCommand(RenderCommandKind kind) : this()
    {
        Kind = kind;
    }

    public static RenderCommand FromBeginPass(int passIndex) =>
        new(RenderCommandKind.BeginPass, new BeginPassPayload(passIndex));

    public static RenderCommand FromHostPass(int index) =>
        new(RenderCommandKind.HostPass, new HostPassPayload(index));

    public static RenderCommand FromEndPass() =>
        new(RenderCommandKind.EndPass);

    public static RenderCommand FromSetPipeline(PipelineHandle pipeline) =>
        new(RenderCommandKind.SetPipeline, new SetPipelinePayload(pipeline));

    public static RenderCommand FromSetVertexBuffer(uint slot, BufferHandle buffer, ulong offset, ulong size) =>
        new(RenderCommandKind.SetVertexBuffer, new SetVertexBufferPayload(slot, buffer, offset, size));

    public static RenderCommand FromSetIndexBuffer(BufferHandle buffer, IndexFormat format, ulong offset, ulong size) =>
        new(RenderCommandKind.SetIndexBuffer, new SetIndexBufferPayload(buffer, format, offset, size));

    public static RenderCommand FromSetBindGroup(uint groupIndex, BindGroupHandle group) =>
        new(RenderCommandKind.SetBindGroup, new SetBindGroupPayload(groupIndex, group, 0, false));

    public static RenderCommand FromSetBindGroup(uint groupIndex, BindGroupHandle group, uint dynamicOffset) =>
        new(RenderCommandKind.SetBindGroup, new SetBindGroupPayload(groupIndex, group, dynamicOffset, true));

    public static RenderCommand FromDraw(in DrawCommand cmd) =>
        new(RenderCommandKind.Draw, cmd);

    public static RenderCommand FromDrawIndexedIndirect(in DrawIndexedIndirectCommand cmd) =>
        new(RenderCommandKind.DrawIndexedIndirect, cmd);

    public static RenderCommand FromDrawIndexed(in DrawIndexedCommand cmd) =>
        new(RenderCommandKind.DrawIndexed, cmd);

    public static RenderCommand FromSetViewport(float x, float y, float width, float height, float minDepth, float maxDepth) =>
        new(RenderCommandKind.SetViewport, new SetViewportPayload(x, y, width, height, minDepth, maxDepth));

    public static RenderCommand FromBeginComputePass() =>
        new(RenderCommandKind.BeginComputePass);

    public static RenderCommand FromEndComputePass() =>
        new(RenderCommandKind.EndComputePass);

    public static RenderCommand FromSetComputePipeline(ComputePipelineHandle pipeline) =>
        new(RenderCommandKind.SetComputePipeline, new SetComputePipelinePayload(pipeline));

    public static RenderCommand FromDispatch(in DispatchCommand cmd) =>
        new(RenderCommandKind.Dispatch, cmd);
}

/// <summary>Append-only sequence of <see cref="RenderCommand"/>s plus a side table of
/// <see cref="RenderPassDesc"/> records referenced by <see cref="RenderCommandKind.BeginPass"/>.</summary>
public readonly struct RenderCommandStream
{
    public ReadOnlyMemory<RenderCommand> Commands { get; init; }

    /// <summary>Native callbacks borrowed for the lifetime of this stream, executed only on a compatible host backend.</summary>
    public ReadOnlyMemory<HostPassInvocation> HostPasses { get; init; }

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
