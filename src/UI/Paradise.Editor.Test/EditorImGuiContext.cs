using System.Numerics;
using Hexa.NET.ImGui;
using Paradise.Editor.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.Test;

/// <summary>A private ImGui context with docking on, torn down on dispose.</summary>
/// <remarks>Its own rather than shared, because a dockspace's whole behaviour turns on whether a
/// node already exists — a suite sharing one context would hand the seeding path to whichever test
/// ran first and test the restore path in all the others. Still <c>[NotInParallel]</c> at every
/// call site: the current context is cimgui's process-global <c>GImGui</c>.</remarks>
public sealed class EditorImGuiContext : IDisposable
{
    private ImGuiContextPtr _context;

    public unsafe EditorImGuiContext(int width = 1600, int height = 1000)
    {
        _context = ImGuiApi.CreateContext();
        ImGuiApi.SetCurrentContext(_context);
        var io = ImGuiApi.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures | ImGuiBackendFlags.RendererHasVtxOffset;
        io.DisplaySize = new Vector2(width, height);
        io.DeltaTime = 1f / 60f;
        io.Fonts.AddFontDefault();
        // ImGui writes imgui.ini on DestroyContext as well as on its timer, so a suite that kept
        // it would leave a file behind AND restore the previous run's layout into the next.
        io.IniFilename = null;
        EditorDockspace.EnableDocking();
    }

    /// <summary>Run one whole ImGui frame around <paramref name="draw"/>.</summary>
    public ImDrawDataPtr Frame(Action draw)
    {
        ImGuiApi.NewFrame();
        draw();
        ImGuiApi.Render();
        return ImGuiApi.GetDrawData();
    }

    public void Dispose()
    {
        if (_context.IsNull) return;
        ImGuiApi.DestroyContext(_context);
        _context = default;
        ImGuiApi.SetCurrentContext(default);
    }
}
