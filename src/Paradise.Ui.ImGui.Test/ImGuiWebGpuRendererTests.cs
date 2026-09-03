using System;
using System.Collections.Generic;
using System.Numerics;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;
using WebGpuSharp;

namespace Paradise.Ui.ImGui.Test;

/// <summary>Snapshot capture against a real ImGui frame (offset rebasing, totals) and the
/// WebGPU renderer end-to-end: a real ImGui window rendered to an offscreen target must produce
/// pixels where the window is, none where the scissor excludes it, and leave the composited
/// background elsewhere. GPU tests skip without an adapter; ImGui work is serialized (one
/// process-global current context).</summary>
[NotInParallel]
public class ImGuiWebGpuRendererTests
{
    private const int Width = 256;
    private const int Height = 256;

    // WebGPU.CreateInstance() throws DllNotFoundException (rather than returning null) when the
    // native Dawn library isn't loadable on this host — matches the skip pattern established in
    // Paradise.Rendering.WebGPU.Test/HeadlessSmokeTests.cs.
    private static Device? TryCreateDevice()
    {
        Instance instance;
        try
        {
            instance = WebGPU.CreateInstance() ?? throw new DllNotFoundException("WebGPU.CreateInstance() returned null.");
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        var options = new RequestAdapterOptions
        {
            CompatibleSurface = null!,
            PowerPreference = PowerPreference.HighPerformance,
            FeatureLevel = FeatureLevel.Core,
        };
        var adapter = instance.RequestAdapterSync(in options, 10_000_000_000UL);
        if (adapter is null) return null;
        var desc = new DeviceDescriptor
        {
            Label = "Paradise.Ui.ImGui.Test",
            UncapturedErrorCallback = static (type, message) =>
                Console.Error.WriteLine($"[ImGuiTest][wgpu {type}] {message.ToString()}"),
        };
        return adapter.RequestDeviceSync(in desc, 10_000_000_000UL);
    }

    [Test]
    public async Task snapshot_concatenates_lists_and_matches_totals()
    {
        using var imgui = new ImGuiTestContext(Width, Height);
        var drawData = imgui.Frame(() => ImGuiTestContext.Panel("hello"));

        // Texture capture first, always: it is what stamps the ImTextureID the draw commands
        // carry, and reading a command's id before that trips an assert inside ImGui.
        ImGuiTextureCapture.CaptureFrom(drawData, new ImGuiTextureOps());
        var snapshot = new ImGuiDrawSnapshot();
        snapshot.Capture(drawData);

        await Assert.That(snapshot.VertexBytes).IsEqualTo(drawData.TotalVtxCount * ImGuiDrawSnapshot.VertexStride);
        await Assert.That(snapshot.IndexBytes).IsEqualTo(drawData.TotalIdxCount * sizeof(ushort));
        await Assert.That(snapshot.CommandCount).IsGreaterThan(0);
        // Every command's index range must land inside the concatenated buffers.
        for (var i = 0; i < snapshot.CommandCount; i++)
        {
            var command = snapshot.Commands[i];
            await Assert.That((int)(command.IndexOffset + command.ElementCount) * sizeof(ushort))
                .IsLessThanOrEqualTo(snapshot.IndexBytes);
        }
    }

    [Test]
    public async Task renders_a_window_over_a_composited_background()
    {
        using var imgui = new ImGuiTestContext(Width, Height);
        var device = TryCreateDevice();
        if (device is null)
        {
            Skip.Test("No WebGPU adapter available.");
            return;
        }
        var queue = device.GetQueue()!;

        var renderer = new ImGuiWebGpuRenderer(device, TextureFormat.RGBA8Unorm);
        var ops = new ImGuiTextureOps();

        // The font atlas reaches the renderer the way it does in production: as texture ops
        // captured off the frame, not as a one-shot atlas upload.
        var drawData = imgui.Frame(() => ImGuiTestContext.Panel("hello"));
        ImGuiTextureCapture.CaptureFrom(drawData, ops);
        var snapshot = new ImGuiDrawSnapshot();
        snapshot.Capture(drawData);
        var pending = new List<ImGuiTextureOp>();
        ops.DrainTo(pending);
        renderer.ApplyTextureOps(pending);

        var pixels = RenderOverGreen(device, renderer, snapshot);

        // Inside the window (title bar region): not the green background.
        await Assert.That(pixels.IsBackgroundAt(100, 50)).IsFalse();
        // Far corner: untouched composited background (green, LoadOp.Load held).
        var outside = pixels.At(Width - 8, Height - 8);
        await Assert.That(outside.R).IsEqualTo((byte)0);
        await Assert.That(Math.Abs(outside.G - 102)).IsLessThan(3); // 0.4 x 255 in a Unorm target
        await Assert.That(outside.B).IsEqualTo((byte)0);
    }

    /// <summary>A Destroy op must not blind the snapshots that still name the texture.
    ///
    /// Regression test. The renderer deferred the GPU object by
    /// <c>DestroyDelayFrames</c> but dropped the id's lookup entry immediately, and a command
    /// whose id resolves to nothing is skipped — so a snapshot still in hand lost every glyph the
    /// moment ImGui asked for the old atlas to go, which is the silent-vanish failure the ops
    /// queue exists to prevent. <c>AcquireForRender</c> returns the same snapshot when nothing new
    /// was published and drains the queue anyway, so this is reachable on any frame where the sim
    /// has captured its ops and not yet published.</summary>
    [Test]
    public async Task a_destroyed_texture_keeps_drawing_until_the_snapshots_naming_it_are_gone()
    {
        using var imgui = new ImGuiTestContext(Width, Height);
        var device = TryCreateDevice();
        if (device is null)
        {
            Skip.Test("No WebGPU adapter available.");
            return;
        }

        var renderer = new ImGuiWebGpuRenderer(device, TextureFormat.RGBA8Unorm);
        var ops = new ImGuiTextureOps();
        var drawData = imgui.Frame(() => ImGuiTestContext.Panel("hello"));
        ImGuiTextureCapture.CaptureFrom(drawData, ops);
        var snapshot = new ImGuiDrawSnapshot();
        snapshot.Capture(drawData);
        var pending = new List<ImGuiTextureOp>();
        ops.DrainTo(pending);
        renderer.ApplyTextureOps(pending);
        var atlasId = snapshot.Commands[0].TextureId;

        await Assert.That(RenderOverGreen(device, renderer, snapshot).IsBackgroundAt(100, 50)).IsFalse();

        // ImGui asks for the atlas back — the frame that would replace this snapshot has not been
        // published yet, so the renderer is still handed the one that names it.
        renderer.ApplyTextureOps([ImGuiTextureOp.Destroy(atlasId)]);
        await Assert.That(RenderOverGreen(device, renderer, snapshot).IsBackgroundAt(100, 50)).IsFalse();

        // Once the delay is spent the texture really is freed, and the stale snapshot draws
        // nothing rather than sampling a destroyed texture.
        renderer.ApplyTextureOps([]);
        renderer.ApplyTextureOps([]);
        await Assert.That(RenderOverGreen(device, renderer, snapshot).IsBackgroundAt(100, 50)).IsTrue();
    }

    private readonly record struct Readback(byte[] Pixels)
    {
        public (byte R, byte G, byte B) At(int x, int y)
        {
            var i = (y * Width + x) * 4;
            return (Pixels[i], Pixels[i + 1], Pixels[i + 2]);
        }

        /// <summary>True when nothing was drawn here — the clear colour survived.</summary>
        public bool IsBackgroundAt(int x, int y)
        {
            var (r, g, b) = At(x, y);
            return r == 0 && b == 0 && Math.Abs(g - 102) < 3; // 0.4 x 255 in a Unorm target
        }
    }

    /// <summary>Clear to green, draw <paramref name="snapshot"/> over it, and read the result
    /// back. Green because <see cref="ImGuiWebGpuRenderer.Render"/> composites with
    /// <c>LoadOp.Load</c>, so "still green" is exactly "this command did not draw".</summary>
    private static Readback RenderOverGreen(Device device, ImGuiWebGpuRenderer renderer, ImGuiDrawSnapshot snapshot)
    {
        var queue = device.GetQueue()!;
        var target = device.CreateTexture(new TextureDescriptor
        {
            Label = "ImGuiTest.Target",
            Size = new Extent3D(Width, Height, 1),
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = TextureDimension.D2,
        })!;
        var targetView = target.CreateView()!;

        var encoder = device.CreateCommandEncoder()!;
        var clearColors = new RenderPassColorAttachment[]
        {
            new()
            {
                View = targetView,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearValue = new WebGpuSharp.Color(0.0, 0.4, 0.0, 1.0),
                DepthSlice = null,
            },
        };
        var clearDesc = new RenderPassDescriptor { ColorAttachments = clearColors };
        encoder.BeginRenderPass(in clearDesc).End();
        renderer.Render(encoder, targetView, Width, Height, snapshot);

        const uint bpp = 4;
        var padded = (Width * bpp + 255u) & ~255u;
        var readback = device.CreateBuffer(new BufferDescriptor
        {
            Label = "ImGuiTest.Readback",
            Size = (ulong)padded * Height,
            Usage = BufferUsage.MapRead | BufferUsage.CopyDst,
            MappedAtCreation = false,
        })!;
        var src = new TexelCopyTextureInfo { Texture = target, MipLevel = 0 };
        var dst = new TexelCopyBufferInfo
        {
            Buffer = readback,
            Layout = new TexelCopyBufferLayout { Offset = 0, BytesPerRow = padded, RowsPerImage = Height },
        };
        var extent = new Extent3D(Width, Height, 1);
        encoder.CopyTextureToBuffer(in src, in dst, in extent);
        queue.Submit(encoder.Finish()!);
        queue.OnSubmittedWorkSync(5_000_000_000UL);

        var pixelsOut = new byte[Width * Height * 4];
        readback.MapSync(MapMode.Read, 0, (nuint)((ulong)padded * Height), 5_000);
        readback.GetConstMappedRange(0, (nuint)((ulong)padded * Height), (ReadOnlySpan<byte> mapped) =>
        {
            for (var y = 0; y < Height; y++)
                mapped.Slice((int)(y * padded), Width * 4).CopyTo(pixelsOut.AsSpan(y * Width * 4));
        });
        readback.Unmap();
        return new Readback(pixelsOut);
    }

    [Test]
    public async Task host_texture_ids_below_the_reserved_floor_are_rejected()
    {
        var device = TryCreateDevice();
        if (device is null)
        {
            Skip.Test("No WebGPU adapter available.");
            return;
        }
        var renderer = new ImGuiWebGpuRenderer(device, TextureFormat.RGBA8Unorm);
        var texture = device.CreateTexture(new TextureDescriptor
        {
            Label = "ImGuiTest.HostTexture",
            Size = new Extent3D(4, 4, 1),
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = TextureDimension.D2,
        })!;
        var view = texture.CreateView()!;

        // An id in ImGui's own space would be overwritten the moment ImGui created a texture
        // with that UniqueID, so it is refused rather than silently shadowed.
        await Assert.That(() => renderer.RegisterTexture(1, view))
            .Throws<ArgumentOutOfRangeException>();
        renderer.RegisterTexture(ImGuiWebGpuRenderer.FirstHostTextureId, view);

        // Unregistering has the same floor, and for a sharper reason: an ImGui-owned texture is
        // retired by its Destroy op, which also frees the GPU object and holds the lookup for the
        // destroy delay. Taking it away here would skip both.
        await Assert.That(() => renderer.UnregisterTexture(1)).Throws<ArgumentOutOfRangeException>();
        renderer.UnregisterTexture(ImGuiWebGpuRenderer.FirstHostTextureId);
        // Idempotent: unregistering what is not there is not an error.
        renderer.UnregisterTexture(ImGuiWebGpuRenderer.FirstHostTextureId);
    }

    /// <summary>Disposal frees what it can and is safe to repeat. It cannot be checked from
    /// outside — WebGPUSharp offers no "is destroyed" — so this pins the contract that matters to
    /// a caller: it does not throw, and a second call is a no-op.</summary>
    [Test]
    public async Task disposing_twice_is_a_no_op()
    {
        using var imgui = new ImGuiTestContext(Width, Height);
        var device = TryCreateDevice();
        if (device is null)
        {
            Skip.Test("No WebGPU adapter available.");
            return;
        }

        var renderer = new ImGuiWebGpuRenderer(device, TextureFormat.RGBA8Unorm);
        var ops = new ImGuiTextureOps();
        var drawData = imgui.Frame(() => ImGuiTestContext.Panel("hello"));
        ImGuiTextureCapture.CaptureFrom(drawData, ops);
        var snapshot = new ImGuiDrawSnapshot();
        snapshot.Capture(drawData);
        var pending = new List<ImGuiTextureOp>();
        ops.DrainTo(pending);
        renderer.ApplyTextureOps(pending);
        // Render once so the vertex/index buffers exist and there is something to free.
        RenderOverGreen(device, renderer, snapshot);

        await Assert.That(() => { renderer.Dispose(); renderer.Dispose(); }).ThrowsNothing();
    }
}
