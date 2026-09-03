using System.Buffers;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Test;

/// <summary>The graph's ordering and lowering rules, without a GPU. These are the claims the
/// renderer's pass declarations rest on, so they are worth asserting where nothing can skip.</summary>
public class FrameGraphTests
{
    private static readonly PassRecorder Nothing = static (object _, ref RenderCommandEncoder _, int _) => { };

    private static PassRecorder MarkWith(int vertexCount) =>
        (object _, ref RenderCommandEncoder e, int argument) =>
            e.Draw(new DrawCommand((uint)(vertexCount + argument), 1, 0, 0));

    private static FrameGraph GraphWithOneColorTarget(out GraphTexture target)
    {
        var graph = new FrameGraph();
        target = graph.ImportColor(new TextureViewHandle(7, 1));
        return graph;
    }

    /// <summary>The reason the event values are spaced: a pass can be placed between two named
    /// stops without the engine having to mint a slot for it.</summary>
    [Test]
    public async Task an_offset_places_a_pass_between_two_named_events()
    {
        var graph = GraphWithOneColorTarget(out var target);
        var writer = new ArrayBufferWriter<RenderCommand>(64);

        // Declared out of order on purpose — the sort, not the declaration order, decides.
        graph.AddRasterPass("after", RenderPassEvent.AfterOpaque)
            .Color(0, target, LoadOp.Load, StoreOp.Store).Record(graph, MarkWith(30));
        graph.AddRasterPass("built-in", RenderPassEvent.Opaque)
            .Color(0, target, LoadOp.Load, StoreOp.Store).Record(graph, MarkWith(10));
        graph.AddRasterPass("injected", RenderPassEvent.Opaque, offset: 1)
            .Color(0, target, LoadOp.Load, StoreOp.Store).Record(graph, MarkWith(20));

        var stream = graph.Compile(writer);

        // Draw vertex counts stand in for identity: 10 (Opaque), 20 (Opaque+1), 30 (AfterOpaque).
        await Assert.That(DrawCounts(stream)).IsEquivalentTo(new uint[] { 10, 20, 30 });
    }

    /// <summary>Ties keep declaration order, so two features registered at the same event run in
    /// the order they were registered rather than an order the sort happens to produce.</summary>
    [Test]
    public async Task passes_at_the_same_event_keep_declaration_order()
    {
        var graph = GraphWithOneColorTarget(out var target);
        var writer = new ArrayBufferWriter<RenderCommand>(64);

        for (var i = 0; i < 8; i++)
        {
            graph.AddRasterPass($"p{i}", RenderPassEvent.Post)
                .Color(0, target, LoadOp.Load, StoreOp.Store)
                .Record(graph, MarkWith(100), argument: i);
        }

        var stream = graph.Compile(writer);
        await Assert.That(DrawCounts(stream)).IsEquivalentTo(new uint[] { 100, 101, 102, 103, 104, 105, 106, 107 });
    }

    /// <summary>BeginPass indices must address the SORTED table, not the declaration order — the
    /// bug the whole design exists to make unrepresentable.</summary>
    [Test]
    public async Task begin_pass_indices_address_the_sorted_pass_table()
    {
        var graph = new FrameGraph();
        var late = graph.ImportColor(new TextureViewHandle(1, 1));
        var early = graph.ImportColor(new TextureViewHandle(2, 1));
        var writer = new ArrayBufferWriter<RenderCommand>(64);

        graph.AddRasterPass("late", RenderPassEvent.Composite)
            .Color(0, late, LoadOp.Load, StoreOp.Store).Record(graph, Nothing);
        graph.AddRasterPass("early", RenderPassEvent.Shadows)
            .Color(0, early, LoadOp.Clear, StoreOp.Discard).Record(graph, Nothing);

        var stream = graph.Compile(writer);

        // Spans cannot cross an await, so everything asserted is lifted out first.
        var passCount = stream.Passes.Length;
        var slot0View = stream.Passes.Span[0][0].ColorView;
        var slot0Store = stream.Passes.Span[0][0].Store;
        var slot1View = stream.Passes.Span[1][0].ColorView;
        var firstKind = stream.Commands.Span[0].Kind;
        var firstIndex = stream.Commands.Span[0].BeginPass.PassIndex;
        var thirdKind = stream.Commands.Span[2].Kind;
        var thirdIndex = stream.Commands.Span[2].BeginPass.PassIndex;

        await Assert.That(passCount).IsEqualTo(2);
        // Slot 0 is the shadow-event pass, and its BeginPass says 0.
        await Assert.That(slot0View).IsEqualTo(new TextureViewHandle(2, 1));
        await Assert.That(slot0Store).IsEqualTo(StoreOp.Discard);
        await Assert.That(slot1View).IsEqualTo(new TextureViewHandle(1, 1));
        await Assert.That(firstKind).IsEqualTo(RenderCommandKind.BeginPass);
        await Assert.That(firstIndex).IsEqualTo(0);
        await Assert.That(thirdKind).IsEqualTo(RenderCommandKind.BeginPass);
        await Assert.That(thirdIndex).IsEqualTo(1);
    }

    /// <summary>The backbuffer resolves to an attachment with no explicit view — which is how the
    /// backend tells "present here" from "render to my texture".</summary>
    [Test]
    public async Task the_backbuffer_resolves_to_an_attachment_without_a_view()
    {
        var graph = new FrameGraph();
        var offscreen = graph.ImportColor(new TextureViewHandle(3, 1));
        var writer = new ArrayBufferWriter<RenderCommand>(32);

        graph.AddRasterPass("offscreen", RenderPassEvent.Post)
            .Color(0, offscreen, LoadOp.Clear, StoreOp.Store).Record(graph, Nothing);
        graph.AddRasterPass("present", RenderPassEvent.Composite)
            .Color(0, FrameGraph.Backbuffer, LoadOp.Clear, StoreOp.Store).Record(graph, Nothing);

        var stream = graph.Compile(writer);
        var offscreenHasView = stream.Passes.Span[0][0].ColorView.IsValid;
        var presentHasView = stream.Passes.Span[1][0].ColorView.IsValid;

        await Assert.That(offscreenHasView).IsTrue();
        await Assert.That(presentHasView).IsFalse();
    }

    /// <summary>A depth attachment carries the texture AND the slice view, because one shadow-map
    /// layer is a view into an array the pass must still name as a whole.</summary>
    [Test]
    public async Task a_depth_attachment_carries_both_the_texture_and_its_slice_view()
    {
        var graph = new FrameGraph();
        var layer = graph.ImportDepth(new TextureHandle(5, 1), new TextureViewHandle(9, 1));
        var writer = new ArrayBufferWriter<RenderCommand>(32);

        graph.AddRasterPass("shadow", RenderPassEvent.Shadows)
            .Depth(layer, LoadOp.Clear, StoreOp.Store, clear: 1f).Record(graph, Nothing);

        var stream = graph.Compile(writer);
        var depth = stream.Passes.Span[0].Depth;
        var colorCount = stream.Passes.Span[0].ColorAttachmentCount;

        await Assert.That(depth.HasValue).IsTrue();
        await Assert.That(depth!.Value.DepthTexture).IsEqualTo(new TextureHandle(5, 1));
        await Assert.That(depth.Value.DepthView).IsEqualTo(new TextureViewHandle(9, 1));
        await Assert.That(colorCount).IsEqualTo(0);
    }

    /// <summary>Reset must leave the backbuffer addressable — it is resource 0 and no declaration
    /// site re-imports it.</summary>
    [Test]
    public async Task reset_clears_passes_and_imports_but_keeps_the_backbuffer()
    {
        var graph = new FrameGraph();
        var writer = new ArrayBufferWriter<RenderCommand>(32);

        graph.AddRasterPass("a", RenderPassEvent.Post)
            .Color(0, graph.ImportColor(new TextureViewHandle(1, 1)), LoadOp.Clear, StoreOp.Store).Record(graph, Nothing);
        _ = graph.Compile(writer);

        graph.Reset();
        writer.ResetWrittenCount();
        graph.AddRasterPass("b", RenderPassEvent.Composite)
            .Color(0, FrameGraph.Backbuffer, LoadOp.Clear, StoreOp.Store).Record(graph, Nothing);

        var stream = graph.Compile(writer);
        var passCount = stream.Passes.Length;
        var isBackbuffer = !stream.Passes.Span[0][0].ColorView.IsValid;

        await Assert.That(passCount).IsEqualTo(1);
        await Assert.That(isBackbuffer).IsTrue();
    }

    /// <summary>A pass that would render nowhere is a declaration mistake, and the message names
    /// it — the whole reason passes carry a name.</summary>
    [Test]
    public async Task a_pass_with_no_attachments_is_rejected_by_name()
    {
        var graph = new FrameGraph();
        var writer = new ArrayBufferWriter<RenderCommand>(16);
        graph.AddRasterPass("Ghost", RenderPassEvent.Post).Record(graph, Nothing);

        await Assert.That(() => graph.Compile(writer))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("Ghost");
    }

    /// <summary>A pass declared but never given a recorder would emit an empty Begin/End pair, so
    /// it is a mistake rather than a no-op.</summary>
    [Test]
    public async Task a_pass_without_a_recorder_is_rejected_by_name()
    {
        var graph = GraphWithOneColorTarget(out var target);
        var writer = new ArrayBufferWriter<RenderCommand>(16);
        graph.AddRasterPass("Forgotten", RenderPassEvent.Post).Color(0, target, LoadOp.Clear, StoreOp.Store);

        await Assert.That(() => graph.Compile(writer))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("Forgotten");
    }

    /// <summary>Color slots must be filled from zero up: a gap would leave an unbound attachment
    /// the backend reads as garbage.</summary>
    [Test]
    public async Task a_skipped_color_slot_is_rejected()
    {
        var graph = GraphWithOneColorTarget(out var target);
        await Assert.That(() => graph.AddRasterPass("gap", RenderPassEvent.Post).Color(2, target, LoadOp.Clear, StoreOp.Store))
            .Throws<ArgumentOutOfRangeException>();
    }

    private static uint[] DrawCounts(in RenderCommandStream stream)
    {
        var counts = new List<uint>();
        foreach (var cmd in stream.Commands.Span)
            if (cmd.Kind == RenderCommandKind.Draw)
                counts.Add(cmd.Draw.VertexCount);
        return [.. counts];
    }
}
