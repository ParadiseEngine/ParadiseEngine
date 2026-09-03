using System.Buffers;
using Paradise.Editor.ImGui;
using Paradise.Rendering;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.Host;

/// <summary>What the editor draws each frame, and the pass the renderer clears behind it.</summary>
/// <remarks>
/// <para>
/// One type for both run modes so the windowed run and the headless capture cannot drift: a
/// screenshot that proves nothing about what a person sees is worse than no screenshot.
/// </para>
/// <para>
/// E0 draws the dockspace and ImGui's own demo window docked into it. The demo is a deliberate
/// choice of subject, not a placeholder to delete later: it exercises far more of ImGui's widget
/// surface — tables, plots, trees, popups, text input — than any panel written by hand at this
/// stage would, so the capture is a real test of the texture protocol and the renderer. E1
/// replaces the seed recipe and adds the shell around it.
/// </para>
/// </remarks>
internal sealed class EditorFrame
{
    private const string DemoWindowTitle = "Dear ImGui Demo";

    private readonly EditorDockspace _dockspace = new(
        "ParadiseEditorDockspace",
        root => EditorDockspace.Dock(DemoWindowTitle, root));

    private bool _showDemo = true;

    public EditorDockspace Dockspace => _dockspace;

    public void Draw()
    {
        _dockspace.Draw();
        if (_showDemo) ImGuiApi.ShowDemoWindow(ref _showDemo);
    }

    /// <summary>Stands in for the scene until E3 gives the central node a render target: one pass
    /// that clears the backbuffer, so the overlay composites over something and the frame goes
    /// through <c>Submit</c> — the path that runs <c>OverlayPass</c> and presents.</summary>
    /// <remarks>The pass has to be RECORDED, not merely described. A stream carrying the
    /// descriptor table and no commands submits nothing at all, and the symptom is an untouched
    /// backbuffer behind a UI that looks perfectly correct.</remarks>
    internal sealed class ClearPass
    {
        private readonly ArrayBufferWriter<RenderCommand> _commands = new(2);
        private readonly RenderPassDesc[] _passes;

        public ClearPass(ColorRgba color)
        {
            _passes = new RenderPassDesc[1];
            _passes[0] = new RenderPassDesc(colorAttachmentCount: 1);
            _passes[0].Colors.Slot0 = new ColorAttachmentDesc(
                View: RenderViewHandle.Invalid, // backbuffer
                Load: LoadOp.Clear,
                Store: StoreOp.Store,
                ClearValue: color);
        }

        public RenderCommandStream Record()
        {
            _commands.ResetWrittenCount();
            var encoder = new RenderCommandEncoder(_commands);
            encoder.BeginPass(0);
            encoder.EndPass();
            return new RenderCommandStream(_commands.WrittenMemory, _passes);
        }
    }
}
