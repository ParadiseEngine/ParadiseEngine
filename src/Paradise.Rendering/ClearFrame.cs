using System.Buffers;

namespace Paradise.Rendering;

/// <summary>Records a reusable frame that clears the backbuffer.</summary>
/// <remarks>Submit the recorded commands so normal presentation and overlay paths run; a pass
/// descriptor without commands performs no clear. Record reuses its buffer without steady-state
/// allocations.</remarks>
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
