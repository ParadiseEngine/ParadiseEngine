using System;
using System.Collections.Generic;

namespace Paradise.Rendering;

/// <summary>Checks a <see cref="RenderCommandStream"/>'s structure before a backend encodes it.</summary>
/// <remarks>Every backend calls this, so a stream that works in one fails in all the others with the same
/// message. Backends keep only what is theirs to check: stale handles and native callback types.
/// Allocation-free; runs once per submission.</remarks>
public static class RenderCommandStreamValidator
{
    /// <summary>The most color attachments a pass may declare on the current backends.</summary>
    public const int MaxSupportedColorAttachments = 1;

    /// <summary>Throws <see cref="InvalidOperationException"/> for a malformed stream.</summary>
    /// <param name="stream">The stream to check.</param>
    /// <param name="allowBackbuffer">False for an offscreen submission, whose passes must name every color target.</param>
    /// <param name="pipelineHasDepth">Whether each live pipeline was built with a depth format; pipelines it
    /// does not know are not checked. Dawn and the browser would report a mismatch only asynchronously.</param>
    /// <exception cref="NotSupportedException">A pass declares more color attachments than the backends support.</exception>
    public static void Validate(in RenderCommandStream stream, bool allowBackbuffer, IReadOnlyDictionary<PipelineHandle, bool>? pipelineHasDepth = null)
    {
        var passes = stream.Passes.Span;
        foreach (ref readonly var pass in passes)
        {
            if (pass.ColorAttachmentCount > MaxSupportedColorAttachments)
            {
                throw new NotSupportedException(
                    $"At most one color attachment per pass is supported (got {pass.ColorAttachmentCount}). " +
                    "Multi-attachment rendering is deferred.");
            }
        }

        var commands = stream.Commands.Span;
        var open = PassKind.None;
        var passHasDepth = false;
        for (var i = 0; i < commands.Length; i++)
        {
            ref readonly var command = ref commands[i];
            switch (command.Kind)
            {
                case RenderCommandKind.HostPass:
                    if (open != PassKind.None)
                        throw new InvalidOperationException("Host callback issued inside an open pass.");
                    if ((uint)command.HostPass.CallbackIndex >= (uint)stream.HostPasses.Length)
                        throw new InvalidOperationException("Host callback index is outside the stream's callback table.");
                    if (!allowBackbuffer && !stream.HostPasses.Span[command.HostPass.CallbackIndex].Target.IsValid)
                        throw new InvalidOperationException("SubmitOffscreen host callbacks require an explicit target.");
                    break;
                case RenderCommandKind.BeginPass:
                {
                    if (open == PassKind.Render)
                        throw new InvalidOperationException("Nested BeginPass — previous pass was not ended (missing EndPass).");
                    if (open == PassKind.Compute)
                        throw new InvalidOperationException("BeginPass inside an open compute pass — end it with EndComputePass first.");
                    var index = command.BeginPass.PassIndex;
                    if ((uint)index >= (uint)passes.Length)
                        throw new InvalidOperationException($"BeginPass references pass index {index} but only {passes.Length} pass(es) declared.");
                    if (!allowBackbuffer && passes[index].ColorAttachmentCount == 1 && !passes[index].Colors.Slot0.ColorView.IsValid)
                    {
                        throw new InvalidOperationException(
                            "SubmitOffscreen streams must render only into explicit ColorView targets — " +
                            "this pass targets the backbuffer. Use Submit for the frame's presenting stream.");
                    }

                    open = PassKind.Render;
                    passHasDepth = passes[index].Depth is not null;
                    break;
                }
                case RenderCommandKind.EndPass:
                    if (open == PassKind.Compute)
                        throw new InvalidOperationException("EndPass inside a compute pass — compute passes close with EndComputePass.");
                    open = PassKind.None;
                    break;
                case RenderCommandKind.BeginComputePass:
                    if (open == PassKind.Render)
                        throw new InvalidOperationException("BeginComputePass inside an open render pass — end it with EndPass first.");
                    if (open == PassKind.Compute)
                        throw new InvalidOperationException("Nested BeginComputePass — previous compute pass was not ended (missing EndComputePass).");
                    open = PassKind.Compute;
                    break;
                case RenderCommandKind.EndComputePass:
                    if (open == PassKind.Render)
                        throw new InvalidOperationException("EndComputePass inside a render pass — render passes close with EndPass.");
                    open = PassKind.None;
                    break;
                case RenderCommandKind.SetPipeline:
                    RequireRenderPass(open);
                    if (pipelineHasDepth is not null
                        && pipelineHasDepth.TryGetValue(command.SetPipeline.Pipeline, out var hasDepth)
                        && hasDepth != passHasDepth)
                    {
                        throw new InvalidOperationException(hasDepth
                            ? "Pipeline was built with a DepthStencilFormat but the active pass has no Depth attachment — attach a depth texture to the pass or build the pipeline without depth."
                            : "The active pass has a Depth attachment but the pipeline was built without a DepthStencilFormat — build the pipeline with a matching depth format or drop the pass's Depth attachment.");
                    }

                    break;
                case RenderCommandKind.SetVertexBuffer:
                case RenderCommandKind.SetIndexBuffer:
                case RenderCommandKind.Draw:
                case RenderCommandKind.DrawIndexed:
                case RenderCommandKind.DrawIndexedIndirect:
                case RenderCommandKind.SetViewport:
                    RequireRenderPass(open);
                    break;
                case RenderCommandKind.SetComputePipeline:
                case RenderCommandKind.Dispatch:
                    RequireComputePass(open);
                    break;
                case RenderCommandKind.SetBindGroup:
                    // Either pass kind: compute and render encoders bind groups the same way.
                    if (open == PassKind.None)
                        throw new InvalidOperationException("Render command issued outside of an active BeginPass/EndPass scope.");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown RenderCommandKind '{command.Kind}'.");
            }
        }

        if (open == PassKind.Render)
            throw new InvalidOperationException("RenderCommandStream ended with an open render pass — missing EndPass.");
        if (open == PassKind.Compute)
            throw new InvalidOperationException("RenderCommandStream ended with an open compute pass — missing EndComputePass.");
    }

    private static void RequireRenderPass(PassKind open)
    {
        if (open == PassKind.Render) return;
        throw new InvalidOperationException(open == PassKind.Compute
            ? "Render command issued inside a compute pass — use SetComputePipeline/Dispatch there."
            : "Render command issued outside of an active BeginPass/EndPass scope.");
    }

    private static void RequireComputePass(PassKind open)
    {
        if (open == PassKind.Compute) return;
        throw new InvalidOperationException(open == PassKind.Render
            ? "Compute command issued inside a render pass — compute commands need a BeginComputePass scope."
            : "Compute command issued outside of an active BeginComputePass/EndComputePass scope.");
    }

    private enum PassKind : byte
    {
        None,
        Render,
        Compute,
    }
}
