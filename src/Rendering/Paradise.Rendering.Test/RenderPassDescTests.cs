namespace Paradise.Rendering.Test;

public class RenderPassDescTests
{
    [Test]
    public async Task inline_color_attachments_round_trip_through_indexer()
    {
        var pass = new RenderPassDesc(colorAttachmentCount: 3);
        var view0 = new RenderViewHandle(10, 1);
        var view1 = new RenderViewHandle(11, 1);
        var view2 = new RenderViewHandle(12, 1);

        pass[0] = new ColorAttachmentDesc(view0, LoadOp.Clear, StoreOp.Store, ColorRgba.Red);
        pass[1] = new ColorAttachmentDesc(view1, LoadOp.Load, StoreOp.Store, ColorRgba.Green);
        pass[2] = new ColorAttachmentDesc(view2, LoadOp.Clear, StoreOp.Discard, ColorRgba.Blue);

        await Assert.That(pass[0].View).IsEqualTo(view0);
        await Assert.That(pass[1].View).IsEqualTo(view1);
        await Assert.That(pass[2].View).IsEqualTo(view2);
        await Assert.That(pass[0].ClearValue).IsEqualTo(ColorRgba.Red);
        await Assert.That(pass[2].Store).IsEqualTo(StoreOp.Discard);
    }

    [Test]
    public async Task color_attachment_span_reflects_count()
    {
        var pass = new RenderPassDesc(colorAttachmentCount: 2);
        pass[0] = new ColorAttachmentDesc(new RenderViewHandle(1, 1), LoadOp.Clear, StoreOp.Store, ColorRgba.White);
        pass[1] = new ColorAttachmentDesc(new RenderViewHandle(2, 1), LoadOp.Load, StoreOp.Store, ColorRgba.Black);

        var (length, view0, view1) = ReadSpan(ref pass);
        await Assert.That(length).IsEqualTo(2);
        await Assert.That(view0).IsEqualTo(new RenderViewHandle(1, 1));
        await Assert.That(view1).IsEqualTo(new RenderViewHandle(2, 1));

        WriteSpanSlot0(ref pass, new ColorAttachmentDesc(new RenderViewHandle(99, 1), LoadOp.Clear, StoreOp.Store, ColorRgba.Red));
        await Assert.That(pass[0].View).IsEqualTo(new RenderViewHandle(99, 1));

        static (int length, RenderViewHandle view0, RenderViewHandle view1) ReadSpan(ref RenderPassDesc pass)
        {
            var span = pass.ColorAttachments;
            return (span.Length, span[0].View, span[1].View);
        }

        static void WriteSpanSlot0(ref RenderPassDesc pass, ColorAttachmentDesc value)
        {
            var span = pass.ColorAttachments;
            span[0] = value;
        }
    }

    [Test]
    [Arguments(-1)]
    [Arguments(RenderPassDesc.MaxColorAttachments + 1)]
    [Arguments(100)]
    public async Task constructor_rejects_color_attachment_count_outside_storage(int count)
    {
        await Assert.That(() => new RenderPassDesc(colorAttachmentCount: count))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(2, -1)]
    [Arguments(2, 2)]
    [Arguments(2, 5)]
    [Arguments(RenderPassDesc.MaxColorAttachments, RenderPassDesc.MaxColorAttachments)]
    public async Task indexer_rejects_reads_and_writes_outside_active_attachments(int count, int index)
    {
        // Bounds follow the active count, even when the index fits in the inline storage.
        await Assert.That(() =>
        {
            var pass = new RenderPassDesc(colorAttachmentCount: count);
            _ = pass[index];
        }).Throws<ArgumentOutOfRangeException>();

        await Assert.That(() =>
        {
            var pass = new RenderPassDesc(colorAttachmentCount: count);
            pass[index] = new ColorAttachmentDesc(new RenderViewHandle(99, 1), LoadOp.Clear, StoreOp.Store, ColorRgba.Red);
        }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(RenderPassDesc.MaxColorAttachments + 1)]
    [Arguments(100)]
    public async Task setter_rejects_color_attachment_count_outside_storage(int count)
    {
        // An unchecked count could expose memory beyond the inline buffer through the span.
        await Assert.That(() =>
        {
            var pass = new RenderPassDesc(colorAttachmentCount: 2);
            pass.ColorAttachmentCount = count;
        }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task color_attachment_count_setter_grows_and_shrinks_within_bounds()
    {
        var pass = new RenderPassDesc(colorAttachmentCount: 2);
        pass[0] = new ColorAttachmentDesc(new RenderViewHandle(1, 1), LoadOp.Clear, StoreOp.Store, ColorRgba.White);
        pass[1] = new ColorAttachmentDesc(new RenderViewHandle(2, 1), LoadOp.Load, StoreOp.Store, ColorRgba.Black);

        pass.ColorAttachmentCount = RenderPassDesc.MaxColorAttachments;
        await Assert.That(pass.ColorAttachmentCount).IsEqualTo(RenderPassDesc.MaxColorAttachments);
        await Assert.That(GetSpanLength(ref pass)).IsEqualTo(RenderPassDesc.MaxColorAttachments);

        pass.ColorAttachmentCount = 0;
        await Assert.That(pass.ColorAttachmentCount).IsEqualTo(0);
        await Assert.That(GetSpanLength(ref pass)).IsEqualTo(0);

        static int GetSpanLength(ref RenderPassDesc pass) => pass.ColorAttachments.Length;
    }

    [Test]
    public async Task raw_colors_write_outside_count_is_invisible_to_count_aware_paths()
    {
        // Backend marshalling can write raw storage without widening the submitted range.
        var pass = new RenderPassDesc(colorAttachmentCount: 2);
        var hidden = new ColorAttachmentDesc(new RenderViewHandle(77, 1), LoadOp.Clear, StoreOp.Store, ColorRgba.Red);
        pass.Colors.Slot7 = hidden;

        await Assert.That(pass.ColorAttachmentCount).IsEqualTo(2);
        await Assert.That(GetSpanLength(ref pass)).IsEqualTo(2);
        await Assert.That(() =>
        {
            var p = new RenderPassDesc(colorAttachmentCount: 2);
            p.Colors.Slot7 = new ColorAttachmentDesc(new RenderViewHandle(77, 1), LoadOp.Clear, StoreOp.Store, ColorRgba.Red);
            _ = p[7];
        }).Throws<ArgumentOutOfRangeException>();

        static int GetSpanLength(ref RenderPassDesc pass) => pass.ColorAttachments.Length;
    }

    [Test]
    public async Task depth_attachment_is_optional()
    {
        var pass = new RenderPassDesc(colorAttachmentCount: 0);
        await Assert.That(pass.Depth.HasValue).IsFalse();

        var depth = new DepthAttachmentDesc(new TextureHandle(5, 1), LoadOp.Clear, StoreOp.Store, 1.0f);
        var pass2 = new RenderPassDesc(colorAttachmentCount: 0, depth: depth);
        await Assert.That(pass2.Depth.HasValue).IsTrue();
        await Assert.That(pass2.Depth!.Value.ClearDepth).IsEqualTo(1.0f);
    }
}
