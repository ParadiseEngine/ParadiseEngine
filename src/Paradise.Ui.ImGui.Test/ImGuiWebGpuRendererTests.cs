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

        (byte R, byte G, byte B) At(int x, int y)
        {
            var i = (y * Width + x) * 4;
            return (pixelsOut[i], pixelsOut[i + 1], pixelsOut[i + 2]);
        }

        // Inside the window (title bar region): not the green background.
        var inside = At(100, 50);
        await Assert.That(inside.G == 102 && inside.R == 0).IsFalse();
        // Far corner: untouched composited background (green, LoadOp.Load held).
        var outside = At(Width - 8, Height - 8);
        await Assert.That(outside.R).IsEqualTo((byte)0);
        await Assert.That(Math.Abs(outside.G - 102)).IsLessThan(3); // 0.4 x 255 in a Unorm target
        await Assert.That(outside.B).IsEqualTo((byte)0);
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
    }
}
