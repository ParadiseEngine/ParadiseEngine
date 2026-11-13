using System;
using System.Collections.Generic;
using Paradise.Rendering;
using Paradise.Rendering.WebGPU;

namespace Paradise.Ui.ImGui.Test;

/// <summary>The renderer-neutral ImGui path on a real device: through <see cref="WebGpuRenderer"/>'s
/// command stream it must draw what <see cref="ImGuiWebGpuRenderer"/> draws natively.</summary>
[NotInParallel]
public class ImGuiRendererGpuTests
{
    private const int Width = 256;
    private const int Height = 256;

    [Test]
    public async Task the_command_stream_overlay_matches_the_native_overlay()
    {
        using var imgui = new ImGuiTestContext(Width, Height);
        WebGpuRenderer backend;
        try
        {
            backend = WebGpuRenderer.CreateHeadless(Width, Height);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            GpuRequirement.Skip($"No WebGPU adapter available: {error.Message}");
            return;
        }

        var device = ImGuiWebGpuRendererTests.TryCreateDevice();
        if (device is null)
        {
            backend.Dispose();
            GpuRequirement.Skip("No WebGPU adapter available.");
            return;
        }

        // One frame, one set of texture ops, handed to both renderers.
        var ops = new ImGuiTextureOps();
        var drawData = imgui.Frame(() => ImGuiTestContext.Panel("hello"));
        ImGuiTextureCapture.CaptureFrom(drawData, ops);
        var snapshot = new ImGuiDrawSnapshot();
        snapshot.Capture(drawData);
        var pending = new List<ImGuiTextureOp>();
        ops.DrainTo(pending);

        using var native = new ImGuiWebGpuRenderer(device, WebGpuSharp.TextureFormat.RGBA8Unorm);
        native.ApplyTextureOps([.. pending]);
        var expected = ImGuiWebGpuRendererTests.RenderOverGreen(device, native, snapshot).Pixels;

        byte[] actual;
        using (backend)
        {
            using var renderer = new ImGuiRenderer(backend);
            renderer.ApplyTextureOps(pending);
            backend.Submit(ImGuiRendererTests.RecordInPass(renderer, snapshot, Width, Height, new ColorRgba(0f, 0.4f, 0f, 1f)));
            actual = backend.ReadbackColor(out var width, out var height);
            await Assert.That(width).IsEqualTo((uint)Width);
            await Assert.That(height).IsEqualTo((uint)Height);
            await Assert.That(backend.ColorFormat).IsEqualTo(TextureFormat.Bgra8Unorm);
        }

        // The window drew, the far corner kept the clear, and every pixel matches the native path
        // (the headless target is BGRA, the native test target RGBA).
        var differing = 0;
        var drawn = 0;
        for (var i = 0; i < expected.Length; i += 4)
        {
            var (r, g, b) = (actual[i + 2], actual[i + 1], actual[i]);
            if (Math.Abs(r - expected[i]) > 1 || Math.Abs(g - expected[i + 1]) > 1 || Math.Abs(b - expected[i + 2]) > 1) differing++;
            if (r != 0 || b != 0 || Math.Abs(g - 102) >= 3) drawn++;
        }

        await Assert.That(drawn).IsGreaterThan(1000);
        await Assert.That(differing).IsEqualTo(0);
    }
}
