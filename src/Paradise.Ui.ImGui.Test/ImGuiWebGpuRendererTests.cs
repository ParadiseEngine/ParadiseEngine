using System;
using System.Numerics;
using ImGuiNET;
using WebGpuSharp;

namespace Paradise.Ui.ImGui.Test;

/// <summary>Snapshot capture against a real ImGui frame (offset rebasing, totals) and the
/// WebGPU renderer end-to-end: a real ImGui window rendered to an offscreen target must
/// produce pixels where the window is, none where the scissor excludes it, and leave the
/// composited background elsewhere. GPU tests skip without an adapter; ImGui work is
/// serialized (one global context).</summary>
[NotInParallel]
public class ImGuiWebGpuRendererTests
{
    private const int Width = 256;
    private const int Height = 256;

    private static bool s_contextReady;

    private static void EnsureImGui()
    {
        if (s_contextReady) return;
        global::ImGuiNET.ImGui.CreateContext();
        var io = global::ImGuiNET.ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.DisplaySize = new Vector2(Width, Height);
        io.Fonts.AddFontDefault();
        io.Fonts.Build();
        io.Fonts.SetTexID(ImGuiWebGpuRenderer.FontTextureId);
        s_contextReady = true;
    }

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

    private static ImGuiDrawSnapshot BuildFrame()
    {
        var io = global::ImGuiNET.ImGui.GetIO();
        io.DeltaTime = 1f / 60f;
        global::ImGuiNET.ImGui.NewFrame();
        global::ImGuiNET.ImGui.SetNextWindowPos(new Vector2(40, 40));
        global::ImGuiNET.ImGui.SetNextWindowSize(new Vector2(140, 100));
        global::ImGuiNET.ImGui.Begin("panel", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);
        global::ImGuiNET.ImGui.Text("hello");
        global::ImGuiNET.ImGui.Button("button");
        global::ImGuiNET.ImGui.End();
        global::ImGuiNET.ImGui.Render();

        var snapshot = new ImGuiDrawSnapshot();
        snapshot.Capture(global::ImGuiNET.ImGui.GetDrawData());
        return snapshot;
    }

    [Test]
    public async Task snapshot_concatenates_lists_and_matches_totals()
    {
        EnsureImGui();
        var snapshot = BuildFrame();
        var drawData = global::ImGuiNET.ImGui.GetDrawData();

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
        EnsureImGui();
        var device = TryCreateDevice();
        if (device is null)
        {
            Skip.Test("No WebGPU adapter available.");
            return;
        }
        var queue = device.GetQueue()!;

        var renderer = new ImGuiWebGpuRenderer(device, TextureFormat.RGBA8Unorm);
        unsafe
        {
            var io = global::ImGuiNET.ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out var width, out var height, out _);
            renderer.SetFontAtlas(new ReadOnlySpan<byte>(pixels, width * height * 4), (uint)width, (uint)height);
        }

        var snapshot = BuildFrame();

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
}
