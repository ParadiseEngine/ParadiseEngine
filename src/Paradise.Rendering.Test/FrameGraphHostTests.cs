using System.Buffers;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Test;

public class FrameGraphHostTests
{
    private sealed class Callback : HostRenderPass;
    private static readonly PassRecorder Nothing = static (object _, ref PassRecording _, int _) => { };

    [Test]
    public async Task compiling_another_writer_preserves_prior_callbacks_and_attachments()
    {
        var graph = new FrameGraph();
        var firstCallback = new Callback();
        var firstView = new TextureViewHandle(7, 1);
        var target = graph.ImportColor(firstView);
        graph.AddRasterPass("scene", RenderPassEvent.Opaque)
            .Color(0, target, LoadOp.Clear).Record(graph, Nothing);
        graph.AddHostPass("overlay", RenderPassEvent.Overlay, firstCallback, target);
        var firstWriter = new ArrayBufferWriter<RenderCommand>();
        var first = graph.Compile(firstWriter);

        graph.Reset();
        var secondCallback = new Callback();
        var secondView = new TextureViewHandle(8, 1);
        target = graph.ImportColor(secondView);
        graph.AddRasterPass("other scene", RenderPassEvent.Opaque)
            .Color(0, target, LoadOp.Clear).Record(graph, Nothing);
        graph.AddHostPass("other overlay", RenderPassEvent.Overlay, secondCallback, target);
        var secondWriter = new ArrayBufferWriter<RenderCommand>();
        var second = graph.Compile(secondWriter);

        await Assert.That(first.HostPasses.Span[0].Callback).IsSameReferenceAs(firstCallback);
        await Assert.That(first.HostPasses.Span[0].Target).IsEqualTo(firstView);
        await Assert.That(first.Passes.Span[0][0].ColorView).IsEqualTo(firstView);
        await Assert.That(second.HostPasses.Span[0].Callback).IsSameReferenceAs(secondCallback);
        await Assert.That(second.Passes.Span[0][0].ColorView).IsEqualTo(secondView);

        // Appending another compilation without resetting the writer cannot invalidate its old tables.
        graph.Compile(firstWriter);
        await Assert.That(first.HostPasses.Span[0].Callback).IsSameReferenceAs(firstCallback);
        await Assert.That(first.Passes.Span[0][0].ColorView).IsEqualTo(firstView);

        firstWriter.ResetWrittenCount();
        graph.Reset();
        graph.Compile(firstWriter);
        await Assert.That(second.HostPasses.Span[0].Callback).IsSameReferenceAs(secondCallback);
        await Assert.That(second.Passes.Span[0][0].ColorView).IsEqualTo(secondView);
    }

    [Test]
    public async Task host_callbacks_are_sorted_between_closed_stream_passes()
    {
        var graph = new FrameGraph();
        var first = new Callback();
        var second = new Callback();
        graph.AddHostPass("first", RenderPassEvent.Overlay, first, FrameGraph.Backbuffer);
        graph.AddHostPass("second", RenderPassEvent.Overlay, second, FrameGraph.Backbuffer);
        graph.AddRasterPass("scene", RenderPassEvent.Opaque)
            .Color(0, FrameGraph.Backbuffer, LoadOp.Clear).Record(graph, Nothing);
        graph.AddRasterPass("after", RenderPassEvent.Overlay, 1)
            .Color(0, FrameGraph.Backbuffer, LoadOp.Load).Record(graph, Nothing);
        var stream = graph.Compile(new ArrayBufferWriter<RenderCommand>());
        var commands = stream.Commands.ToArray();

        await Assert.That(graph.LivePassNames.ToArray()).IsEquivalentTo(new[] { "scene", "first", "second", "after" });
        await Assert.That(commands.Select(c => c.Kind).ToArray()).IsEquivalentTo(new[]
        {
            RenderCommandKind.BeginPass, RenderCommandKind.EndPass,
            RenderCommandKind.HostPass, RenderCommandKind.HostPass,
            RenderCommandKind.BeginPass, RenderCommandKind.EndPass,
        });
        await Assert.That(commands[2].HostPass.CallbackIndex).IsEqualTo(0);
        await Assert.That(commands[3].HostPass.CallbackIndex).IsEqualTo(1);
        await Assert.That(commands[4].BeginPass.PassIndex).IsEqualTo(1);
        await Assert.That(stream.Passes.Length).IsEqualTo(2);
        await Assert.That(stream.HostPasses.Span[0].Callback).IsSameReferenceAs(first);
        await Assert.That(stream.HostPasses.Span[1].Callback).IsSameReferenceAs(second);
    }

    [Test]
    public async Task host_target_is_culled_when_unread_and_keeps_its_producer_when_read()
    {
        var graph = new FrameGraph();
        var view = new TextureViewHandle(7, 1);
        var target = graph.ImportColor(view, GraphResourceScope.GraphOnly);
        graph.AddRasterPass("producer", RenderPassEvent.Opaque)
            .Color(0, target, LoadOp.Clear).Record(graph, Nothing);
        graph.AddHostPass("overlay", RenderPassEvent.Overlay, new Callback(), target);
        var writer = new ArrayBufferWriter<RenderCommand>();
        var culled = graph.Compile(writer);
        await Assert.That(culled.Commands.Length).IsEqualTo(0);
        await Assert.That(culled.HostPasses.Length).IsEqualTo(0);

        graph.AddRasterPass("consumer", RenderPassEvent.Overlay, 1)
            .Color(0, FrameGraph.Backbuffer, LoadOp.Clear).Reads(target).Record(graph, Nothing);
        writer.ResetWrittenCount();
        var live = graph.Compile(writer);
        await Assert.That(graph.CulledPassCount).IsEqualTo(0);
        await Assert.That(live.HostPasses.Span[0].Target).IsEqualTo(view);
        await Assert.That(live.Passes.Span[0][0].Store).IsEqualTo(StoreOp.Store);

        graph.Reset();
        writer.ResetWrittenCount();
        var empty = graph.Compile(writer);
        await Assert.That(empty.HostPasses.Length).IsEqualTo(0);
    }
}
