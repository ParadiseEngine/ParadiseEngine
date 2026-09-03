using System;
using System.Numerics;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Ui.ImGui.Sample;

/// <summary>What the sample draws. Runs on the SIM half, between NewFrame and Render, so it may
/// touch sim state directly — here that is just a counter and some widget state.</summary>
internal sealed class SamplePanels
{
    private readonly string _fontDescription;
    private float _slider = 0.35f;
    private bool _showDemo = true;
    private int _frames;

    public SamplePanels(string fontDescription) => _fontDescription = fontDescription;

    public void Draw()
    {
        _frames++;

        ImGuiApi.SetNextWindowPos(new Vector2(24, 24), ImGuiCond.FirstUseEver);
        ImGuiApi.Begin("Paradise.Ui.ImGui", ImGuiWindowFlags.AlwaysAutoResize);

        ImGuiText.Show($"Dear ImGui {ImGuiApi.GetVersionS()} via Hexa.NET.ImGui");
        ImGuiText.Disabled(_fontDescription);
        ImGuiApi.Separator();

        // Every glyph below is rasterized ON DEMAND under the 1.92 texture protocol: nothing here
        // was declared as a range, and the atlas grows through Create/Update ops as the text
        // widens. The CJK line is the one that proves it — it needs a real font AND a glyph the
        // first frame never asked for.
        ImGuiText.Show("ASCII: The quick brown fox jumps over the lazy dog");
        ImGuiText.Show("CJK:   中文字体按需光栅化");
        ImGuiText.Show("Symbols: °±µ¶·×÷ αβγδε ←↑→↓");
        ImGuiApi.Separator();

        // Percent signs and braces are the reason ImGuiText exists: ImGui.Text would hand this
        // string to cimgui's printf and an unmatched %s segfaults the process.
        ImGuiText.Colored(new Vector4(0.45f, 0.85f, 1f, 1f), $"100% safe: {_slider:P0} of a {{format}} string");
        ImGuiText.Wrapped(
            "TextUnformatted plus the style stack, because TextColored and TextWrapped both take a "
            + "printf format and this text is not a literal under our control.");
        ImGuiApi.Separator();

        ImGuiApi.SliderFloat("slider", ref _slider, 0f, 1f);
        ImGuiApi.Checkbox("ImGui demo window", ref _showDemo);
        ImGuiText.Show($"frame {_frames}   {ImGuiApi.GetIO().Framerate:F1} FPS");
        ImGuiApi.End();

        // The demo window is the real smoke test: it exercises far more of ImGui's widget surface
        // — tables, plots, trees, popups, text input — than anything written by hand here would.
        if (_showDemo) ImGuiApi.ShowDemoWindow(ref _showDemo);
    }
}
