using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Paradise.Rendering;

namespace Paradise.Rendering.Pbr.Test.Baseline;

/// <summary>Renders a captured <see cref="RenderCommandStream"/> as deterministic, reviewable text:
/// the pass table with its attachment wiring, and each pass's command sequence.
///
/// This is the load-bearing half of the frame-graph baseline. Pixels can only say "something
/// changed"; the signature says which pass, which attachment, which load op — and it is identical
/// on every adapter, because nothing in it comes from the GPU.
///
/// Handles are printed as first-appearance ordinals (<c>pipeline#0</c>, <c>bg#3</c>) rather than
/// raw <c>(Index, Generation)</c> pairs. Raw values churn whenever an unrelated resource is created
/// earlier in construction, which would make every golden file conflict on every change; ordinals
/// keep exactly the property worth asserting — that pass 5 binds the same pipeline pass 3 did.</summary>
internal static class FrameSignature
{
    /// <summary>Format one captured frame. <paramref name="label"/> heads the file so a golden is
    /// self-describing when it turns up in a diff.</summary>
    internal static string Format(string label, in RecordingRenderer.CapturedFrame frame)
    {
        var sb = new StringBuilder(4096);
        var ids = new HandleOrdinals();

        sb.Append("# ").Append(label).Append('\n');
        sb.Append("submit ").Append(frame.Presenting ? "presenting" : "offscreen")
          .Append(" passes=").Append(frame.Passes.Length)
          .Append(" commands=").Append(frame.Commands.Length).Append('\n');

        for (var i = 0; i < frame.Passes.Length; i++)
        {
            AppendPassDeclaration(sb, i, frame.Passes[i], ids);
        }

        sb.Append('\n');
        AppendCommands(sb, frame, ids);
        return sb.ToString();
    }

    private static void AppendPassDeclaration(StringBuilder sb, int index, RenderPassDesc pass, HandleOrdinals ids)
    {
        sb.Append("pass ").Append(index.ToString("D2", CultureInfo.InvariantCulture))
          .Append("  colors=").Append(pass.ColorAttachmentCount);

        for (var c = 0; c < pass.ColorAttachmentCount; c++)
        {
            var a = pass[c];
            sb.Append("\n         color").Append(c).Append(' ')
              .Append(a.ColorView.IsValid ? ids.View(a.ColorView) : a.View.IsValid ? ids.RenderView(a.View) : "backbuffer")
              .Append(" load=").Append(a.Load)
              .Append(" store=").Append(a.Store)
              .Append(" clear=").Append(Rgba(a.ClearValue));
        }

        if (pass.Depth is { } d)
        {
            sb.Append("\n         depth ").Append(ids.Texture(d.DepthTexture))
              .Append(d.DepthView.IsValid ? " " + ids.View(d.DepthView) : " (default view)")
              .Append(" load=").Append(d.DepthLoad)
              .Append(" store=").Append(d.DepthStore)
              .Append(" clear=").Append(d.ClearDepth.ToString("0.###", CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append("\n         depth none");
        }

        sb.Append('\n');
    }

    private static void AppendCommands(StringBuilder sb, in RecordingRenderer.CapturedFrame frame, HandleOrdinals ids)
    {
        // Draw calls are collapsed into runs: a shadow layer issuing the same indexed draw for 24
        // casters is 24 identical lines that say nothing, and a golden nobody reads is a golden
        // nobody notices going wrong. The run count itself is asserted, so a lost draw still fails.
        var pending = new List<string>();
        var runLine = string.Empty;
        var runCount = 0;

        void FlushRun()
        {
            if (runCount == 0) return;
            pending.Add(runCount == 1 ? runLine : $"{runLine} x{runCount}");
            runCount = 0;
        }

        void Emit(string line)
        {
            if (line == runLine) { runCount++; return; }
            FlushRun();
            runLine = line;
            runCount = 1;
        }

        foreach (var cmd in frame.Commands)
        {
            Emit(Describe(cmd, ids));
        }
        FlushRun();

        var indent = "  ";
        foreach (var line in pending)
        {
            if (line.StartsWith("BeginPass", StringComparison.Ordinal) ||
                line.StartsWith("BeginComputePass", StringComparison.Ordinal))
            {
                sb.Append(line).Append('\n');
                indent = "  ";
                continue;
            }
            if (line.StartsWith("EndPass", StringComparison.Ordinal) ||
                line.StartsWith("EndComputePass", StringComparison.Ordinal))
            {
                sb.Append(line).Append('\n');
                continue;
            }
            sb.Append(indent).Append(line).Append('\n');
        }
    }

    private static string Describe(in RenderCommand cmd, HandleOrdinals ids) => cmd.Kind switch
    {
        RenderCommandKind.BeginPass =>
            $"BeginPass {cmd.BeginPass.PassIndex.ToString("D2", CultureInfo.InvariantCulture)}",
        RenderCommandKind.EndPass => "EndPass",
        RenderCommandKind.BeginComputePass => "BeginComputePass",
        RenderCommandKind.EndComputePass => "EndComputePass",
        RenderCommandKind.SetPipeline =>
            $"SetPipeline {ids.Pipeline(cmd.SetPipeline.Pipeline)}",
        RenderCommandKind.SetComputePipeline =>
            $"SetComputePipeline {ids.ComputePipeline(cmd.SetComputePipeline.Pipeline)}",
        RenderCommandKind.SetBindGroup =>
            $"SetBindGroup {cmd.SetBindGroup.GroupIndex} {ids.BindGroup(cmd.SetBindGroup.Group)}" +
            (cmd.SetBindGroup.HasDynamicOffset ? $" dyn={cmd.SetBindGroup.DynamicOffset}" : string.Empty),
        RenderCommandKind.SetVertexBuffer =>
            $"SetVertexBuffer {cmd.SetVertexBuffer.Slot} {ids.Buffer(cmd.SetVertexBuffer.Buffer)} " +
            $"offset={cmd.SetVertexBuffer.Offset} size={cmd.SetVertexBuffer.Size}",
        RenderCommandKind.SetIndexBuffer =>
            $"SetIndexBuffer {ids.Buffer(cmd.SetIndexBuffer.Buffer)} {cmd.SetIndexBuffer.Format} " +
            $"offset={cmd.SetIndexBuffer.Offset} size={cmd.SetIndexBuffer.Size}",
        RenderCommandKind.SetViewport =>
            $"SetViewport {F(cmd.SetViewport.X)},{F(cmd.SetViewport.Y)} " +
            $"{F(cmd.SetViewport.Width)}x{F(cmd.SetViewport.Height)} " +
            $"depth={F(cmd.SetViewport.MinDepth)}..{F(cmd.SetViewport.MaxDepth)}",
        RenderCommandKind.Draw =>
            $"Draw verts={cmd.Draw.VertexCount} inst={cmd.Draw.InstanceCount} " +
            $"first={cmd.Draw.FirstVertex}/{cmd.Draw.FirstInstance}",
        RenderCommandKind.DrawIndexed =>
            $"DrawIndexed idx={cmd.DrawIndexed.IndexCount} inst={cmd.DrawIndexed.InstanceCount} " +
            $"first={cmd.DrawIndexed.FirstIndex} base={cmd.DrawIndexed.BaseVertex} " +
            $"firstInst={cmd.DrawIndexed.FirstInstance}",
        RenderCommandKind.Dispatch =>
            $"Dispatch {cmd.Dispatch.WorkgroupCountX}x{cmd.Dispatch.WorkgroupCountY}x{cmd.Dispatch.WorkgroupCountZ}",
        _ => $"<{cmd.Kind}>",
    };

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Rgba(ColorRgba c) =>
        $"({F(c.R)},{F(c.G)},{F(c.B)},{F(c.A)})";

    /// <summary>Assigns each distinct handle a stable ordinal in first-appearance order, per frame.
    /// Separate counters per handle kind so the names read as what they are.</summary>
    private sealed class HandleOrdinals
    {
        private readonly Dictionary<(string Kind, uint Index, uint Generation), string> _names = [];
        private readonly Dictionary<string, int> _next = [];

        private string Get(string kind, uint index, uint generation)
        {
            var key = (kind, index, generation);
            if (_names.TryGetValue(key, out var existing)) return existing;
            var n = _next.TryGetValue(kind, out var c) ? c : 0;
            _next[kind] = n + 1;
            var name = $"{kind}#{n}";
            _names[key] = name;
            return name;
        }

        internal string Pipeline(PipelineHandle h) => Get("pipeline", h.Index, h.Generation);
        internal string ComputePipeline(ComputePipelineHandle h) => Get("cpipeline", h.Index, h.Generation);
        internal string BindGroup(BindGroupHandle h) => Get("bg", h.Index, h.Generation);
        internal string Buffer(BufferHandle h) => Get("buf", h.Index, h.Generation);
        internal string Texture(TextureHandle h) => Get("tex", h.Index, h.Generation);
        internal string View(TextureViewHandle h) => Get("view", h.Index, h.Generation);
        internal string RenderView(RenderViewHandle h) => Get("rview", h.Index, h.Generation);
    }
}
