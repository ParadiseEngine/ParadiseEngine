using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Paradise.Rendering;

namespace Paradise.Ui.ImGui.Test;

/// <summary>The renderer-neutral ImGui path, checked against a recording <see cref="IRenderer"/>:
/// the command stream it records, the texture protocol and what it frees.</summary>
[NotInParallel]
public class ImGuiRendererTests
{
    private const int Width = 256;
    private const int Height = 256;

    [Test]
    public async Task a_real_frame_records_a_valid_stream_with_one_scissored_draw_per_command()
    {
        using var imgui = new ImGuiTestContext(Width, Height);
        var backend = new RecordingRenderer();
        using var renderer = new ImGuiRenderer(backend);
        var snapshot = Capture(imgui, renderer);

        var stream = RecordInPass(renderer, snapshot, Width, Height);
        backend.Submit(stream);

        var kinds = stream.Commands.ToArray().Select(command => command.Kind).ToArray();
        await Assert.That(kinds.Count(kind => kind == RenderCommandKind.DrawIndexed)).IsEqualTo(snapshot.CommandCount);
        await Assert.That(kinds.Count(kind => kind == RenderCommandKind.SetScissorRect)).IsEqualTo(snapshot.CommandCount);
        await Assert.That(kinds[1]).IsEqualTo(RenderCommandKind.SetPipeline);
        // The atlas is the only texture, so it is bound once however many commands sample it.
        await Assert.That(kinds.Count(kind => kind == RenderCommandKind.SetBindGroup)).IsEqualTo(1);
        await Assert.That(backend.Pipelines.Single().Blend).IsEqualTo(BlendMode.AlphaBlend);
        await Assert.That(backend.Pipelines.Single().Format).IsEqualTo(backend.ColorFormat);
    }

    [Test]
    public async Task clip_rectangles_are_scaled_and_clamped_to_the_target()
    {
        await Assert.That(ImGuiRenderer.Scissor(new Vector4(-10, -10, 300, 50), Vector2.Zero, Vector2.One, 256, 256))
            .IsEqualTo(new SetScissorRectPayload(0, 0, 256, 50));
        await Assert.That(ImGuiRenderer.Scissor(new Vector4(10, 20, 30, 40), new Vector2(5, 5), new Vector2(2, 2), 256, 256))
            .IsEqualTo(new SetScissorRectPayload(10, 30, 40, 40));
        await Assert.That(ImGuiRenderer.Scissor(new Vector4(300, 0, 400, 10), Vector2.Zero, Vector2.One, 256, 256)).IsNull();
        await Assert.That(ImGuiRenderer.Scissor(new Vector4(10, 10, 10, 50), Vector2.Zero, Vector2.One, 256, 256)).IsNull();
    }

    [Test]
    public async Task an_update_writes_only_its_rectangle_at_its_origin()
    {
        var backend = new RecordingRenderer();
        using var renderer = new ImGuiRenderer(backend);

        renderer.ApplyTextureOps([
            ImGuiTextureOp.Create(1, 8, 8, new byte[8 * 8 * 4]),
            ImGuiTextureOp.Update(1, 2, 3, 4, 2, new byte[4 * 2 * 4]),
        ]);

        await Assert.That(backend.Writes.Count).IsEqualTo(2);
        await Assert.That(backend.Writes[0]).IsEqualTo(backend.Writes[0] with { X = 0, Y = 0, Width = 8, Height = 8, BytesPerRow = 32, Bytes = 256 });
        await Assert.That(backend.Writes[1]).IsEqualTo(backend.Writes[0] with { X = 2, Y = 3, Width = 4, Height = 2, BytesPerRow = 16, Bytes = 32 });
        await Assert.That(() => renderer.ApplyTextureOps([ImGuiTextureOp.Update(9, 0, 0, 1, 1, new byte[4])]))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task a_destroyed_texture_keeps_drawing_for_the_delay_then_is_freed_and_skipped()
    {
        var backend = new RecordingRenderer();
        using var renderer = new ImGuiRenderer(backend);
        renderer.ApplyTextureOps([ImGuiTextureOp.Create(1, 4, 4, new byte[64])]);
        var snapshot = Triangle(textureId: 1);

        await Assert.That(Draws(RecordInPass(renderer, snapshot, Width, Height))).IsEqualTo(1);

        renderer.ApplyTextureOps([ImGuiTextureOp.Destroy(1)]);
        await Assert.That(Draws(RecordInPass(renderer, snapshot, Width, Height))).IsEqualTo(1);
        renderer.ApplyTextureOps([]);
        await Assert.That(Draws(RecordInPass(renderer, snapshot, Width, Height))).IsEqualTo(1);
        var textures = backend.LiveTextures.Count;

        renderer.ApplyTextureOps([]);
        await Assert.That(backend.LiveTextures.Count).IsEqualTo(textures - 1);
        await Assert.That(Draws(RecordInPass(renderer, snapshot, Width, Height))).IsEqualTo(0);
    }

    [Test]
    public async Task host_textures_bind_their_view_and_respect_the_reserved_floor()
    {
        var backend = new RecordingRenderer();
        using var renderer = new ImGuiRenderer(backend);
        var view = new TextureViewHandle(99, 1);

        await Assert.That(() => renderer.RegisterTexture(1, view)).Throws<ArgumentOutOfRangeException>();
        renderer.RegisterTexture(ImGuiRenderer.FirstHostTextureId, view);
        await Assert.That(Draws(RecordInPass(renderer, Triangle(ImGuiRenderer.FirstHostTextureId), Width, Height))).IsEqualTo(1);

        var entries = backend.LiveBindGroups.Values.Single().Entries.ToArray();
        await Assert.That(entries[1]).IsEqualTo(BindGroupEntryDesc.ForTextureView(1, view));

        await Assert.That(() => renderer.UnregisterTexture(1)).Throws<ArgumentOutOfRangeException>();
        renderer.UnregisterTexture(ImGuiRenderer.FirstHostTextureId);
        await Assert.That(backend.LiveBindGroups.Count).IsEqualTo(0);
        await Assert.That(Draws(RecordInPass(renderer, Triangle(ImGuiRenderer.FirstHostTextureId), Width, Height))).IsEqualTo(0);
    }

    [Test]
    public async Task disposing_frees_everything_it_created_once()
    {
        using var imgui = new ImGuiTestContext(Width, Height);
        var backend = new RecordingRenderer();
        var renderer = new ImGuiRenderer(backend);
        var snapshot = Capture(imgui, renderer);
        RecordInPass(renderer, snapshot, Width, Height);
        renderer.ApplyTextureOps([ImGuiTextureOp.Destroy(snapshot.Commands[0].TextureId)]);

        await Assert.That(backend.LiveCount).IsGreaterThan(0);
        renderer.Dispose();
        renderer.Dispose();
        await Assert.That(backend.LiveCount).IsEqualTo(0);
    }

    internal static ImGuiDrawSnapshot Capture(ImGuiTestContext imgui, ImGuiRenderer renderer)
    {
        var ops = new ImGuiTextureOps();
        var drawData = imgui.Frame(() => ImGuiTestContext.Panel("hello"));
        ImGuiTextureCapture.CaptureFrom(drawData, ops);
        var snapshot = new ImGuiDrawSnapshot();
        snapshot.Capture(drawData);
        var pending = new List<ImGuiTextureOp>();
        ops.DrainTo(pending);
        renderer.ApplyTextureOps(pending);
        return snapshot;
    }

    /// <summary>Records the snapshot into one backbuffer pass that keeps what is there.</summary>
    internal static RenderCommandStream RecordInPass(ImGuiRenderer renderer, ImGuiDrawSnapshot snapshot, uint width, uint height, ColorRgba? clear = null)
    {
        var writer = new ArrayBufferWriter<RenderCommand>();
        var encoder = new RenderCommandEncoder(writer);
        var pass = new RenderPassDesc(1);
        pass[0] = new ColorAttachmentDesc(default, clear is null ? LoadOp.Load : LoadOp.Clear, StoreOp.Store, clear ?? ColorRgba.Black);
        encoder.BeginPass(0);
        renderer.Record(ref encoder, snapshot, width, height);
        encoder.EndPass();
        return new RenderCommandStream(writer.WrittenMemory, new[] { pass });
    }

    private static int Draws(in RenderCommandStream stream)
    {
        var count = 0;
        foreach (var command in stream.Commands.Span)
            if (command.Kind == RenderCommandKind.DrawIndexed) count++;
        return count;
    }

    /// <summary>One triangle in the top-left corner sampling <paramref name="textureId"/>.</summary>
    private static ImGuiDrawSnapshot Triangle(ulong textureId) => new()
    {
        Vertices = new byte[1024],
        VertexBytes = 3 * ImGuiDrawSnapshot.VertexStride,
        Indices = new byte[1024],
        IndexBytes = 3 * sizeof(ushort),
        Commands = [new ImGuiDrawSnapshot.Command(new Vector4(0, 0, 16, 16), textureId, 0, 0, 3)],
        CommandCount = 1,
        DisplaySize = new Vector2(Width, Height),
        FramebufferScale = Vector2.One,
    };
}
