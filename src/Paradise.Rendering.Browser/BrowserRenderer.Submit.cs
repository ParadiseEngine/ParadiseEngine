using System;
using System.Buffers.Binary;
using System.Runtime.Versioning;

namespace Paradise.Rendering.Browser;

/// <summary>Frame submission: the whole <see cref="RenderCommandStream"/> is re-encoded into one
/// flat little-endian buffer and handed to the shim in a SINGLE interop call, which then walks it
/// with a decoder loop.</summary>
/// <remarks>
/// <para>Why re-encode rather than ship <c>RenderCommand</c>'s own 48-byte cells: the JS side must
/// address GPU objects by table index, and only this side can turn an <c>(Index, Generation)</c>
/// handle into one — resolving here keeps the stale-handle contract intact (a destroyed handle
/// throws instead of silently addressing whatever now occupies its slot) and keeps the shim free of
/// any dependency on the managed struct layout. The walk that re-encodes is the same walk that
/// validates, so it costs one pass over the stream, not two.</para>
/// <para>The alternative — one interop call per render command, as the spike did — costs a JS
/// boundary crossing per draw; a PBR frame issues thousands.</para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserRenderer
{
    // Record strides for the frame buffer, mirrored by paradise-webgpu.js. Fixed-size records keep
    // the JS decoder a flat indexed loop with no cursor bookkeeping.
    private const int PassStride = 64;
    private const int OpStride = 48;

    private byte[] _frameBuffer = new byte[OpStride * 256];

    /// <summary>Encode and submit one frame's worth of passes and commands.</summary>
    /// <exception cref="InvalidOperationException">The stream is malformed: nested or unclosed
    /// passes, commands issued outside a pass, an out-of-range pass index, or a pipeline whose
    /// depth state does not match the active pass.</exception>
    /// <exception cref="StaleHandleException">A command references a destroyed resource.</exception>
    public void Submit(in RenderCommandStream stream) => SubmitCore(in stream, allowBackbuffer: true);

    /// <summary>Offscreen-only submit. The JS side would happily take it unchanged (its
    /// getCurrentTexture is lazy and per-frame), but the backbuffer check runs here anyway for
    /// parity with the Dawn backend — a stream that works in the browser and dies on desktop is
    /// exactly the bug class the shared validation exists to prevent.</summary>
    public void SubmitOffscreen(in RenderCommandStream stream) => SubmitCore(in stream, allowBackbuffer: false);

    private enum PassKind : byte { None, Render, Compute }

    private void SubmitCore(in RenderCommandStream stream, bool allowBackbuffer)
    {
        ThrowIfDisposed();

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
        var inPass = PassKind.None;
        var passHasDepth = false;
        for (var i = 0; i < commands.Length; i++)
        {
            ref readonly var cmd = ref commands[i];
            var op = frame.Slice(opBase + i * OpStride, OpStride);
            op[0] = (byte)cmd.Kind;
            switch (cmd.Kind)
            {
                case RenderCommandKind.BeginPass:
                {
                    if (inPass == PassKind.Render)
                        throw new InvalidOperationException(
                            "Nested BeginPass — previous pass was not ended (missing EndPass).");
                    if (inPass == PassKind.Compute)
                        throw new InvalidOperationException(
                            "BeginPass inside an open compute pass — end it with EndComputePass first.");
                    var passIndex = cmd.BeginPass.PassIndex;
                    if ((uint)passIndex >= (uint)passes.Length)
                        throw new InvalidOperationException(
                            $"BeginPass references pass index {passIndex} but only {passes.Length} pass(es) declared.");
                    if (!allowBackbuffer && passes[passIndex].ColorAttachmentCount == 1
                        && !passes[passIndex].Colors.Slot0.ColorView.IsValid)
                        throw new InvalidOperationException(
                            "SubmitOffscreen streams must render only into explicit ColorView targets — " +
                            "this pass targets the backbuffer. Use Submit for the frame's presenting stream.");
                    WriteU32(op, 4, (uint)passIndex);
                    inPass = PassKind.Render;
                    passHasDepth = passes[passIndex].Depth is not null;
                    break;
                }
                case RenderCommandKind.EndPass:
                    if (inPass == PassKind.Compute)
                        throw new InvalidOperationException(
                            "EndPass inside a compute pass — compute passes close with EndComputePass.");
                    inPass = PassKind.None;
                    break;
                case RenderCommandKind.BeginComputePass:
                    if (inPass == PassKind.Compute)
                        throw new InvalidOperationException(
                            "Nested BeginComputePass — previous compute pass was not ended (missing EndComputePass).");
                    if (inPass == PassKind.Render)
                        throw new InvalidOperationException(
                            "BeginComputePass inside an open render pass — end it with EndPass first.");
                    inPass = PassKind.Compute;
                    break;
                case RenderCommandKind.EndComputePass:
                    if (inPass == PassKind.Render)
                        throw new InvalidOperationException(
                            "EndComputePass inside a render pass — render passes close with EndPass.");
                    inPass = PassKind.None;
                    break;
                case RenderCommandKind.SetComputePipeline:
                {
                    RequireComputePass(inPass);
                    var handle = cmd.SetComputePipeline.Pipeline;
                    WriteU32(op, 4, _computePipelines.Resolve(handle.Index, handle.Generation, "ComputePipeline"));
                    break;
                }
                case RenderCommandKind.Dispatch:
                {
                    RequireComputePass(inPass);
                    var d = cmd.Dispatch;
                    WriteU32(op, 4, d.WorkgroupCountX);
                    WriteU32(op, 8, d.WorkgroupCountY);
                    WriteU32(op, 12, d.WorkgroupCountZ);
                    break;
                }
                case RenderCommandKind.SetPipeline:
                {
                    RequireRenderPass(inPass);
                    var handle = cmd.SetPipeline.Pipeline;
                    // Surfaced here because WebGPU would only report it asynchronously, through
                    // the uncaptured-error event, long after the offending call.
                    if (_pipelineHasDepth.TryGetValue(handle, out var pipelineHasDepth) && pipelineHasDepth != passHasDepth)
                    {
                        throw new InvalidOperationException(pipelineHasDepth
                            ? "Pipeline was built with a DepthStencilFormat but the active pass has no Depth attachment — attach a depth texture to the pass or build the pipeline without depth."
                            : "The active pass has a Depth attachment but the pipeline was built without a DepthStencilFormat — build the pipeline with a matching depth format or drop the pass's Depth attachment.");
                    }
                    WriteU32(op, 4, _pipelines.Resolve(handle.Index, handle.Generation, "Pipeline"));
                    break;
                }
                case RenderCommandKind.SetVertexBuffer:
                {
                    RequireRenderPass(inPass);
                    var p = cmd.SetVertexBuffer;
                    WriteU32(op, 4, p.Slot);
                    WriteU32(op, 8, _buffers.Resolve(p.Buffer.Index, p.Buffer.Generation, "Buffer"));
                    WriteF64(op, 32, p.Offset);
                    WriteF64(op, 40, p.Size);
                    break;
                }
                case RenderCommandKind.SetIndexBuffer:
                {
                    RequireRenderPass(inPass);
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
                    if (inPass == PassKind.None)
                        throw new InvalidOperationException(
                            "Render command issued outside of an active BeginPass/EndPass scope.");
                    var p = cmd.SetBindGroup;
                    WriteU32(op, 4, p.GroupIndex);
                    WriteU32(op, 8, _bindGroups.Resolve(p.Group.Index, p.Group.Generation, "BindGroup"));
                    WriteU32(op, 12, p.HasDynamicOffset ? 1u : 0u);
                    WriteU32(op, 16, p.DynamicOffset);
                    break;
                }
                case RenderCommandKind.Draw:
                {
                    RequireRenderPass(inPass);
                    var d = cmd.Draw;
                    WriteU32(op, 4, d.VertexCount);
                    WriteU32(op, 8, d.InstanceCount);
                    WriteU32(op, 12, d.FirstVertex);
                    WriteU32(op, 16, d.FirstInstance);
                    break;
                }
                case RenderCommandKind.DrawIndexed:
                {
                    RequireRenderPass(inPass);
                    var d = cmd.DrawIndexed;
                    WriteU32(op, 4, d.IndexCount);
                    WriteU32(op, 8, d.InstanceCount);
                    WriteU32(op, 12, d.FirstIndex);
                    WriteI32(op, 16, d.BaseVertex);
                    WriteU32(op, 20, d.FirstInstance);
                    break;
                }
                case RenderCommandKind.SetViewport:
                {
                    RequireRenderPass(inPass);
                    var v = cmd.SetViewport;
                    WriteF32(op, 4, v.X);
                    WriteF32(op, 8, v.Y);
                    WriteF32(op, 12, v.Width);
                    WriteF32(op, 16, v.Height);
                    WriteF32(op, 20, v.MinDepth);
                    WriteF32(op, 24, v.MaxDepth);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown RenderCommandKind '{cmd.Kind}'.");
            }
        }

        if (inPass == PassKind.Render)
            throw new InvalidOperationException("RenderCommandStream ended with an open render pass — missing EndPass.");
        if (inPass == PassKind.Compute)
            throw new InvalidOperationException("RenderCommandStream ended with an open compute pass — missing EndComputePass.");

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

    // Explicit switches rather than a cast so a future LoadOp/StoreOp member breaks the build here
    // instead of silently encoding as Load/Store.
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

    private static void RequireRenderPass(PassKind inPass)
    {
        if (inPass == PassKind.Render) return;
        throw new InvalidOperationException(inPass == PassKind.Compute
            ? "Render command issued inside a compute pass — use SetComputePipeline/Dispatch there."
            : "Render command issued outside of an active BeginPass/EndPass scope.");
    }

    private static void RequireComputePass(PassKind inPass)
    {
        if (inPass == PassKind.Compute) return;
        throw new InvalidOperationException(inPass == PassKind.Render
            ? "Compute command issued inside a render pass — compute commands need a BeginComputePass scope."
            : "Compute command issued outside of an active BeginComputePass/EndComputePass scope.");
    }

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
