using System.Buffers;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering;

/// <summary>Records a reusable frame graph that clears the backbuffer.</summary>
public sealed class ClearFrame
{
    private readonly ArrayBufferWriter<RenderCommand> _commands = new(2);

    public ClearFrame(ColorRgba color)
    {
        Graph.AddRasterPass("Clear", RenderPassEvent.Opaque)
            .Color(0, FrameGraph.Backbuffer, LoadOp.Clear, clear: color)
            .Record(this, static (ClearFrame _, ref PassRecording _, int _) => { });
    }

    /// <summary>The persistent graph, to which hosts can append overlay passes before recording.</summary>
    public FrameGraph Graph { get; } = new();

    /// <summary>The stream to hand to Submit, valid until the next call.</summary>
    public RenderCommandStream Record()
    {
        _commands.ResetWrittenCount();
        return Graph.Compile(_commands);
    }
}
