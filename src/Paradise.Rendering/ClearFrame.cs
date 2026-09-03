using System.Buffers;

namespace Paradise.Rendering;

/// <summary>A one-pass frame that clears the backbuffer and nothing else.</summary>
/// <remarks>
/// <para>
/// What a host submits while it has no scene yet: an editor before its viewport exists, a sample,
/// a smoke test. It matters that this goes through <c>Submit</c> rather than a renderer's own
/// clear helper, because <c>Submit</c> is the path that runs the overlay pass and presents — a
/// UI composited over a frame that was never submitted is a UI over an untouched backbuffer.
/// </para>
/// <para>
/// The pass has to be RECORDED, not merely described. A stream carrying the descriptor table and
/// no commands submits nothing at all, and the symptom is subtle: the overlay still appears, over
/// whatever the backbuffer happened to hold. It cost a red clear colour that did not show up to
/// find the first time.
/// </para>
/// <para>
/// Reusable across frames — <see cref="Record"/> resets and refills the same buffer, so a loop
/// calling it every frame allocates nothing.
/// </para>
/// </remarks>
public sealed class ClearFrame
{
    private readonly ArrayBufferWriter<RenderCommand> _commands = new(2);
    private readonly RenderPassDesc[] _passes;

    public ClearFrame(ColorRgba color)
    {
        _passes = new RenderPassDesc[1];
        _passes[0] = new RenderPassDesc(colorAttachmentCount: 1);
        _passes[0].Colors.Slot0 = new ColorAttachmentDesc(
            View: RenderViewHandle.Invalid, // backbuffer
            Load: LoadOp.Clear,
            Store: StoreOp.Store,
            ClearValue: color);
    }

    /// <summary>The stream to hand to <c>Submit</c>. Valid until the next call.</summary>
    public RenderCommandStream Record()
    {
        _commands.ResetWrittenCount();
        var encoder = new RenderCommandEncoder(_commands);
        encoder.BeginPass(0);
        encoder.EndPass();
        return new RenderCommandStream(_commands.WrittenMemory, _passes);
    }
}
