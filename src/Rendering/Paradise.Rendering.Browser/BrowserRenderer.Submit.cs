using System;
using System.Buffers.Binary;
using System.Runtime.Versioning;

namespace Paradise.Rendering.Browser;

/// <summary>Submits a validated command stream to JavaScript as one little-endian buffer.</summary>
/// <remarks>Managed handles resolve to JS resource indices while encoding, preserving stale-handle
/// checks without exposing the managed struct layout. One interop call avoids per-draw boundary
/// crossings.</remarks>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserRenderer
{
    private const int PassStride = BrowserFrameLayout.PassStride;
    private const int OpStride = BrowserFrameLayout.OpStride;

    private byte[] _frameBuffer = new byte[OpStride * 256];

    /// <summary>Encode and submit one frame's worth of passes and commands.</summary>
    /// <exception cref="InvalidOperationException">The stream is malformed: nested or unclosed
    /// passes, commands issued outside a pass, an out-of-range pass index, or a pipeline whose
    /// depth state does not match the active pass.</exception>
    /// <exception cref="StaleHandleException">A command references a destroyed resource.</exception>
    public void Submit(in RenderCommandStream stream) => SubmitCore(in stream, allowBackbuffer: true);

    /// <summary>Offscreen-only submit. The JS side would happily take it unchanged (its
    /// getCurrentTexture is lazy and per-frame), but the backbuffer check runs anyway: both backends
    /// share <see cref="RenderCommandStreamValidator"/>, so a stream that works in the browser cannot
    /// die on desktop.</summary>
    public void SubmitOffscreen(in RenderCommandStream stream) => SubmitCore(in stream, allowBackbuffer: false);

    private void SubmitCore(in RenderCommandStream stream, bool allowBackbuffer)
    {
        ThrowIfDisposed();
        RenderCommandStreamValidator.Validate(in stream, allowBackbuffer, _pipelineHasDepth);

        var passes = stream.Passes.Span;
        var commands = stream.Commands.Span;
        var required = passes.Length * PassStride + commands.Length * OpStride;
        if (_frameBuffer.Length < required)
            Array.Resize(ref _frameBuffer, Math.Max(required, _frameBuffer.Length * 2));
        var frame = _frameBuffer.AsSpan(0, required);
        frame.Clear();

        for (var i = 0; i < passes.Length; i++)
            EncodePass(frame.Slice(i * PassStride, PassStride), passes[i]);

        var opBase = passes.Length * PassStride;
        for (var i = 0; i < commands.Length; i++)
        {
            ref readonly var cmd = ref commands[i];
            var op = frame.Slice(opBase + i * OpStride, OpStride);
            op[0] = (byte)cmd.Kind;
            switch (cmd.Kind)
            {
                // The browser skips native host callbacks; the opcode alone keeps the op table aligned.
                case RenderCommandKind.HostPass:
                case RenderCommandKind.EndPass:
                case RenderCommandKind.BeginComputePass:
                case RenderCommandKind.EndComputePass:
                    break;
                case RenderCommandKind.BeginPass:
                    WriteU32(op, 4, (uint)cmd.BeginPass.PassIndex);
                    break;
                case RenderCommandKind.SetComputePipeline:
                {
                    var handle = cmd.SetComputePipeline.Pipeline;
                    WriteU32(op, 4, _computePipelines.Resolve(handle.Index, handle.Generation, "ComputePipeline"));
                    break;
                }
                case RenderCommandKind.Dispatch:
                {
                    var d = cmd.Dispatch;
                    WriteU32(op, 4, d.WorkgroupCountX);
                    WriteU32(op, 8, d.WorkgroupCountY);
                    WriteU32(op, 12, d.WorkgroupCountZ);
                    break;
                }
                case RenderCommandKind.SetPipeline:
                {
                    var handle = cmd.SetPipeline.Pipeline;
                    WriteU32(op, 4, _pipelines.Resolve(handle.Index, handle.Generation, "Pipeline"));
                    break;
                }
                case RenderCommandKind.SetVertexBuffer:
                {
                    var p = cmd.SetVertexBuffer;
                    WriteU32(op, 4, p.Slot);
                    WriteU32(op, 8, _buffers.Resolve(p.Buffer.Index, p.Buffer.Generation, "Buffer"));
                    WriteF64(op, 32, p.Offset);
                    WriteF64(op, 40, p.Size);
                    break;
                }
                case RenderCommandKind.SetIndexBuffer:
                {
                    var p = cmd.SetIndexBuffer;
                    WriteU32(op, 4, _buffers.Resolve(p.Buffer.Index, p.Buffer.Generation, "Buffer"));
                    WriteU32(op, 8, p.Format == IndexFormat.Uint16 ? 0u : 1u);
                    WriteF64(op, 32, p.Offset);
                    WriteF64(op, 40, p.Size);
                    break;
                }
                case RenderCommandKind.SetBindGroup:
                {
                    // Pass-kind agnostic: GPUComputePassEncoder.setBindGroup has the same shape.
                    var p = cmd.SetBindGroup;
                    WriteU32(op, 4, p.GroupIndex);
                    WriteU32(op, 8, _bindGroups.Resolve(p.Group.Index, p.Group.Generation, "BindGroup"));
                    WriteU32(op, 12, p.HasDynamicOffset ? 1u : 0u);
                    WriteU32(op, 16, p.DynamicOffset);
                    break;
                }
                case RenderCommandKind.Draw:
                {
                    var d = cmd.Draw;
                    WriteU32(op, 4, d.VertexCount);
                    WriteU32(op, 8, d.InstanceCount);
                    WriteU32(op, 12, d.FirstVertex);
                    WriteU32(op, 16, d.FirstInstance);
                    break;
                }
                case RenderCommandKind.DrawIndexed:
                {
                    var d = cmd.DrawIndexed;
                    WriteU32(op, 4, d.IndexCount);
                    WriteU32(op, 8, d.InstanceCount);
                    WriteU32(op, 12, d.FirstIndex);
                    WriteI32(op, 16, d.BaseVertex);
                    WriteU32(op, 20, d.FirstInstance);
                    break;
                }
                case RenderCommandKind.DrawIndexedIndirect:
                {
                    var d = cmd.DrawIndexedIndirect;
                    WriteU32(op, 4, _buffers.Resolve(d.Buffer.Index, d.Buffer.Generation, "Buffer"));
                    WriteF64(op, 32, d.Offset);
                    break;
                }
                case RenderCommandKind.SetViewport:
                {
                    var v = cmd.SetViewport;
                    WriteF32(op, 4, v.X);
                    WriteF32(op, 8, v.Y);
                    WriteF32(op, 12, v.Width);
                    WriteF32(op, 16, v.Height);
                    WriteF32(op, 20, v.MinDepth);
                    WriteF32(op, 24, v.MaxDepth);
                    break;
                }
                case RenderCommandKind.SetScissorRect:
                {
                    var s = cmd.SetScissorRect;
                    WriteU32(op, 4, s.X);
                    WriteU32(op, 8, s.Y);
                    WriteU32(op, 12, s.Width);
                    WriteU32(op, 16, s.Height);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown RenderCommandKind '{cmd.Kind}'.");
            }
        }

        SubmitFrameJs(new ArraySegment<byte>(_frameBuffer, 0, required), passes.Length, commands.Length);
    }

    private void EncodePass(Span<byte> record, RenderPassDesc pass)
    {
        // Either zero color attachments (a depth-only pass, e.g. a shadow layer fill) or one —
        // targeting the backbuffer, or an offscreen view when ColorView is set. Multi-attachment
        // rendering is deferred on both backends.
        var colorCount = pass.ColorAttachmentCount;
        if (colorCount > 1)
            throw new NotSupportedException(
                $"At most one color attachment per pass is supported (got {colorCount}). " +
                "Multi-attachment rendering is deferred.");

        WriteU32(record, 0, (uint)colorCount);
        if (colorCount == 1)
        {
            var color = pass.Colors.Slot0;
            WriteU32(record, 4, (uint)LoadOpCode(color.Load));
            WriteU32(record, 8, (uint)StoreOpCode(color.Store));
            WriteI32(record, 12, color.ColorView.IsValid
                ? (int)_textureViews.Resolve(color.ColorView.Index, color.ColorView.Generation, "TextureView")
                : -1);
            WriteF32(record, 16, (float)color.ClearValue.R);
            WriteF32(record, 20, (float)color.ClearValue.G);
            WriteF32(record, 24, (float)color.ClearValue.B);
            WriteF32(record, 28, (float)color.ClearValue.A);
        }

        if (pass.Depth is not { } depth) return;
        WriteU32(record, 32, 1u);
        WriteU32(record, 36, _textures.Resolve(depth.DepthTexture.Index, depth.DepthTexture.Generation, "Texture"));
        // An explicit view renders into one layer of a depth array; without one the texture's own
        // full view is the target.
        WriteI32(record, 40, depth.DepthView.IsValid
            ? (int)_textureViews.Resolve(depth.DepthView.Index, depth.DepthView.Generation, "TextureView")
            : -1);
        WriteU32(record, 44, (uint)LoadOpCode(depth.DepthLoad));
        WriteU32(record, 48, (uint)StoreOpCode(depth.DepthStore));
        WriteF32(record, 52, depth.ClearDepth);
    }

    // Unknown enum values fail during encoding rather than silently becoming Load/Store.
    private static int LoadOpCode(LoadOp op) => op switch
    {
        LoadOp.Load => 0,
        LoadOp.Clear => 1,
        _ => throw new NotSupportedException($"LoadOp '{op}' has no WebGPU mapping."),
    };

    private static int StoreOpCode(StoreOp op) => op switch
    {
        StoreOp.Store => 0,
        StoreOp.Discard => 1,
        _ => throw new NotSupportedException($"StoreOp '{op}' has no WebGPU mapping."),
    };

    private static void WriteU32(Span<byte> record, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(record[offset..], value);

    private static void WriteI32(Span<byte> record, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(record[offset..], value);

    private static void WriteF32(Span<byte> record, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(record[offset..], value);

    // Buffer offsets and sizes ride as doubles: they are ulong on the contract, and a JS number
    // represents every value a real GPU buffer can address exactly.
    private static void WriteF64(Span<byte> record, int offset, ulong value) =>
        BinaryPrimitives.WriteDoubleLittleEndian(record[offset..], value);
}

/// <summary>Record strides of the frame buffer, mirrored by paradise-webgpu.js.</summary>
/// <remarks>Fixed-size records keep the JS decoder a flat indexed loop with no cursor bookkeeping.
/// Platform-neutral so the wire-contract tests can read them off the browser.</remarks>
internal static class BrowserFrameLayout
{
    public const int PassStride = 64;
    public const int OpStride = 48;
}
