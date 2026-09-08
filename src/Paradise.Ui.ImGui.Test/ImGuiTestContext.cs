using System;
using System.Numerics;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Ui.ImGui.Test;

/// <summary>Owns a fresh ImGui context for one test.</summary>
/// <remarks>Each context exposes its own first-frame atlas Create request. Callers remain
/// NotInParallel because cimgui stores the current context globally.</remarks>
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
        // Same choice ImGuiUiCoreTests makes: ImGui writes imgui.ini on DestroyContext, and a
        // suite that keeps it restores the previous run.s window layout.
        io.IniFilename = null;
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
