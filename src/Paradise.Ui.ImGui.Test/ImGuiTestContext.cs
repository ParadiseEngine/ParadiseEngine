using System;
using System.Numerics;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Ui.ImGui.Test;

/// <summary>A private ImGui context for one test, torn down on dispose.
///
/// Per-test rather than shared-static, because the texture protocol is only observable from a
/// context's FIRST frame: the font atlas reports <c>WantCreate</c> exactly once, and a suite
/// sharing one context would hand that frame to whichever test happened to run first. Contexts
/// are cheap and carry their own atlas, so each test gets a clean state machine.
///
/// Still <c>[NotInParallel]</c> at every call site: the current context lives in cimgui's
/// process-global <c>GImGui</c>, so two tests running at once would fight over it.</summary>
public sealed class ImGuiTestContext : IDisposable
{
    private ImGuiContextPtr _context;

    public unsafe ImGuiTestContext(int width, int height)
    {
        _context = ImGuiApi.CreateContext();
        ImGuiApi.SetCurrentContext(_context);
        var io = ImGuiApi.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures | ImGuiBackendFlags.RendererHasVtxOffset;
        io.DisplaySize = new Vector2(width, height);
        io.DeltaTime = 1f / 60f;
        io.Fonts.AddFontDefault();
    }

    /// <summary>Run one whole ImGui frame around <paramref name="draw"/> and return its draw
    /// data, valid until the next call.</summary>
    public ImDrawDataPtr Frame(Action draw)
    {
        ImGuiApi.NewFrame();
        draw();
        ImGuiApi.Render();
        return ImGuiApi.GetDrawData();
    }

    /// <summary>A window with a few widgets in it — enough geometry for the renderer to draw and
    /// enough text to pull glyphs into the atlas.</summary>
    public static void Panel(string text)
    {
        ImGuiApi.SetNextWindowPos(new Vector2(40, 40));
        ImGuiApi.SetNextWindowSize(new Vector2(140, 100));
        ImGuiApi.Begin("panel", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);
        ImGuiText.Show(text);
        ImGuiApi.Button("button");
        ImGuiApi.End();
    }

    public void Dispose()
    {
        if (_context.IsNull) return;
        ImGuiApi.DestroyContext(_context);
        _context = default;
        ImGuiApi.SetCurrentContext(default);
    }
}
