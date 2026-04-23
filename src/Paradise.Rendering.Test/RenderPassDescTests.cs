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
    public async Task color_attachment_count_above_max_throws()
    {
        await Assert.That(() => new RenderPassDesc(colorAttachmentCount: RenderPassDesc.MaxColorAttachments + 1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task indexer_above_count_throws_even_within_max_storage()
    {
        // Regression: prior to OpenCara F1, writes at indices in [count, MaxColorAttachments)
        // succeeded silently and were invisible to the count-aware span. The count-aware indexer
        // now refuses such accesses.
        await Assert.That(() =>
        {
            var pass = new RenderPassDesc(colorAttachmentCount: 2);
            _ = pass[5];
        }).Throws<ArgumentOutOfRangeException>();

        await Assert.That(() =>
        {
            var pass = new RenderPassDesc(colorAttachmentCount: 2);
            pass[5] = new ColorAttachmentDesc(new RenderViewHandle(99, 1), LoadOp.Clear, StoreOp.Store, ColorRgba.Red);
        }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task indexer_at_or_above_max_throws()
    {
        await Assert.That(() =>
        {
            var pass = new RenderPassDesc(colorAttachmentCount: RenderPassDesc.MaxColorAttachments);
            _ = pass[RenderPassDesc.MaxColorAttachments];
        }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task color_attachment_count_setter_rejects_negative()
    {
        // Regression for OpenCara F1-prime: the prior public mutable field allowed
        // pass.ColorAttachmentCount = -1, and (uint)(-1) == 0xFFFFFFFF made the count-aware
        // indexer's `(uint)index >= (uint)Count` check vacuous. The validated setter closes that.
        await Assert.That(() =>
        {
            var pass = new RenderPassDesc(colorAttachmentCount: 2);
            pass.ColorAttachmentCount = -1;
        }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task color_attachment_count_setter_rejects_above_max()
    {
        // Regression for OpenCara F1-prime: the prior public mutable field allowed
        // pass.ColorAttachmentCount = 100, after which MemoryMarshal.CreateSpan returned a
        // 100-element span over the 8-slot inline buffer. The validated setter closes that.
        await Assert.That(() =>
        {
            var pass = new RenderPassDesc(colorAttachmentCount: 2);
            pass.ColorAttachmentCount = RenderPassDesc.MaxColorAttachments + 1;
        }).Throws<ArgumentOutOfRangeException>();

        await Assert.That(() =>
        {
            var pass = new RenderPassDesc(colorAttachmentCount: 2);
            pass.ColorAttachmentCount = 100;
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
