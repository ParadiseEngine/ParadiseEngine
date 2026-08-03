using System;
using System.Linq;
using WebGpuSharp;
using System.Globalization;

namespace Paradise.Ui.Noesis.Test;

/// <summary>The managed WebGPU RenderDevice against real Noesis + a real (headless) WebGPU
/// adapter: the catalog must match the SDK's own tables, every generated WGSL variant must
/// pass backend validation, and a XAML tree exercising solid/linear/radial paints, PPAA,
/// stencil masking (geometry Clip) and an opacity group must produce the expected pixels
/// through texture readback. GPU tests skip when no adapter is available.</summary>
[NotInParallel]
public class NoesisRenderDeviceTests
{
    private const int Width = 256;
    private const int Height = 256;

    /// <summary>The square the dense-field repro's filled path occupies; pixel comparisons skip it.</summary>
    private const int PathCorner = 96;

    private static bool s_noesisInitialized;

    private static void EnsureNoesis()
    {
        if (s_noesisInitialized) return;
        var name = Environment.GetEnvironmentVariable("NOESIS_LICENSE_NAME");
        var key = Environment.GetEnvironmentVariable("NOESIS_LICENSE_KEY");
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(key))
        {
            global::Noesis.GUI.SetLicense(name, key);
        }
        global::Noesis.GUI.Init();
        s_noesisInitialized = true;
    }

    private static Device? TryCreateDevice()
    {
        try
        {
            var instance = WebGPU.CreateInstance();
            if (instance is null) return null;
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
                Label = "Paradise.Ui.Noesis.Test",
                UncapturedErrorCallback = static (type, message) =>
                    Console.Error.WriteLine($"[NoesisTest][wgpu {type}] {message.ToString()}"),
            };
            return adapter.RequestDeviceSync(in desc, 10_000_000_000UL);
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native library not loadable on this host: {ex.Message}");
            return null;
        }
    }

    [Test]
    public async Task catalog_matches_the_sdk_shader_tables()
    {
        // Enum count includes the Count sentinel; variants exclude it (no GUI.Init needed —
        // the Shader tables are static data).
        var enumNames = Enum.GetNames(typeof(global::Noesis.Shader.Enum)).Where(n => n != "Count").ToArray();
        await Assert.That(NoesisShaderCatalog.Variants.Length).IsEqualTo(enumNames.Length);

        foreach (var name in enumNames)
        {
            var value = (global::Noesis.Shader.Enum)Enum.Parse(typeof(global::Noesis.Shader.Enum), name);
            var index = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            var variant = NoesisShaderCatalog.Variants[index];
            await Assert.That(variant.Name).IsEqualTo(name);

            var vertex = global::Noesis.Shader.VertexForShader(value);
            var format = global::Noesis.Shader.FormatForVertex(vertex);
            await Assert.That(variant.Stride).IsEqualTo(global::Noesis.Shader.SizeForFormat(format));
            await Assert.That((int)variant.Attrs).IsEqualTo(global::Noesis.Shader.AttributesForFormat(format));
        }
    }

    [Test]
    public async Task every_generated_wgsl_variant_passes_backend_validation()
    {
        EnsureNoesis(); // the device is a Noesis BaseComponent — native init must precede it
        var device = TryCreateDevice();
        if (device is null)
        {
            Skip.Test("No WebGPU adapter available.");
            return;
        }
        using var noesisDevice = new NoesisRenderDevice(device, WebGpuSharp.TextureFormat.RGBA8Unorm);
        var count = noesisDevice.PrewarmPipelines();
        // 52 supported variants x 2 states + 3 masking states. Dawn validates each pipeline
        // synchronously — an invalid WGSL port fails here, not at first draw.
        await Assert.That(count).IsEqualTo(52 * 2 + 3);
        await Assert.That(noesisDevice.Unsupported).IsEmpty();
    }

    [Test]
    public async Task create_texture_reads_a_per_mip_pointer_array()
    {
        EnsureNoesis();
        var device = TryCreateDevice();
        if (device is null)
        {
            Skip.Test("No WebGPU adapter available.");
            return;
        }
        using var noesisDevice = new NoesisRenderDevice(device, WebGpuSharp.TextureFormat.RGBA8Unorm);

        // The native contract is `const void** data`: one pointer per mip level, NOT one
        // contiguous allocation (regression: treating it as pixels SIGBUSed on the first
        // mipmapped image bank-heist's production UI loaded). Allocate two exact-size levels
        // with nothing readable behind them and hand over the pointer array.
        var level0 = new byte[8 * 8 * 4];
        var level1 = new byte[4 * 4 * 4];
        Array.Fill(level0, (byte)0x40);
        Array.Fill(level1, (byte)0x80);
        var pin0 = System.Runtime.InteropServices.GCHandle.Alloc(level0, System.Runtime.InteropServices.GCHandleType.Pinned);
        var pin1 = System.Runtime.InteropServices.GCHandle.Alloc(level1, System.Runtime.InteropServices.GCHandleType.Pinned);
        var pointers = new IntPtr[] { pin0.AddrOfPinnedObject(), pin1.AddrOfPinnedObject() };
        var pinTable = System.Runtime.InteropServices.GCHandle.Alloc(pointers, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var texture = noesisDevice.CreateTexture(
                "MipContract", 8, 8, 2, global::Noesis.TextureFormat.RGBA8, pinTable.AddrOfPinnedObject());
            await Assert.That(texture.Width).IsEqualTo(8u);
            await Assert.That(texture.HasMipMaps).IsTrue();
            device.GetQueue()!.OnSubmittedWorkSync(5_000_000_000UL); // uploads flushed, no fault
        }
        finally
        {
            pinTable.Free();
            pin0.Free();
            pin1.Free();
        }
    }

    [Test]
    public async Task xaml_with_masks_gradients_and_opacity_groups_renders()
    {
        var device = TryCreateDevice();
        if (device is null)
        {
            Skip.Test("No WebGPU adapter available.");
            return;
        }
        EnsureNoesis();

        const string xaml = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  Background="Transparent">
              <!-- linear gradient card -->
              <Border Width="200" Height="140" CornerRadius="16"
                      HorizontalAlignment="Center" VerticalAlignment="Center">
                <Border.Background>
                  <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                    <GradientStop Color="#FF3050F0" Offset="0"/>
                    <GradientStop Color="#FF00C8A0" Offset="1"/>
                  </LinearGradientBrush>
                </Border.Background>
              </Border>
              <!-- radial gradient -->
              <Ellipse Width="90" Height="90" Margin="0,0,120,90"
                       HorizontalAlignment="Center" VerticalAlignment="Center">
                <Ellipse.Fill>
                  <RadialGradientBrush>
                    <GradientStop Color="#FFFFD34E" Offset="0"/>
                    <GradientStop Color="#00FFD34E" Offset="1"/>
                  </RadialGradientBrush>
                </Ellipse.Fill>
              </Ellipse>
              <!-- geometry clip: exercises the stencil mask path -->
              <Rectangle Width="120" Height="120" Fill="#FFFF4E6A" Margin="120,110,0,0"
                         HorizontalAlignment="Center" VerticalAlignment="Center">
                <Rectangle.Clip>
                  <EllipseGeometry Center="60,60" RadiusX="55" RadiusY="40"/>
                </Rectangle.Clip>
              </Rectangle>
              <!-- opacity group: exercises the offscreen render-target path -->
              <Grid Opacity="0.5" Margin="0,150,150,0"
                    HorizontalAlignment="Center" VerticalAlignment="Center">
                <Rectangle Width="70" Height="70" Fill="#FFFFFFFF"/>
                <Ellipse Width="70" Height="70" Fill="#FF2090FF" Margin="35,35,0,0"/>
              </Grid>
            </Grid>
            """;

        var root = (global::Noesis.FrameworkElement)global::Noesis.GUI.ParseXaml(xaml);
        var view = global::Noesis.GUI.CreateView(root);
        view.SetFlags(global::Noesis.RenderFlags.PPAA);
        view.SetSize(Width, Height);

        using var noesisDevice = new NoesisRenderDevice(device, WebGpuSharp.TextureFormat.RGBA8Unorm);
        view.Renderer.Init(noesisDevice);

        var target = device.CreateTexture(new TextureDescriptor
        {
            Label = "NoesisTest.Target",
            Size = new Extent3D(Width, Height, 1),
            Format = WebGpuSharp.TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = TextureDimension.D2,
        })!;
        var targetView = target.CreateView()!;
        var queue = device.GetQueue()!;

        view.Update(0.0);
        view.Renderer.UpdateRenderTree();

        var encoder = device.CreateCommandEncoder()!;
        // Scene stand-in: clear to opaque dark gray so LoadOp.Load compositing is observable.
        var clearColors = new RenderPassColorAttachment[]
        {
            new()
            {
                View = targetView,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearValue = new WebGpuSharp.Color(0.2, 0.2, 0.2, 1.0),
                DepthSlice = null,
            },
        };
        var clearDesc = new RenderPassDescriptor { ColorAttachments = clearColors };
        encoder.BeginRenderPass(in clearDesc).End();

        noesisDevice.BeginFrame(encoder, targetView, Width, Height);
        view.Renderer.RenderOffscreen();
        view.Renderer.Render();
        noesisDevice.EndFrame();

        // Readback.
        const uint bpp = 4;
        var padded = (Width * bpp + 255u) & ~255u;
        var readback = device.CreateBuffer(new BufferDescriptor
        {
            Label = "NoesisTest.Readback",
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

        var pixels = new byte[Width * Height * 4];
        readback.MapSync(MapMode.Read, 0, (nuint)((ulong)padded * Height), 5_000);
        readback.GetConstMappedRange(0, (nuint)((ulong)padded * Height), (ReadOnlySpan<byte> mapped) =>
        {
            for (var y = 0; y < Height; y++)
                mapped.Slice((int)(y * padded), Width * 4).CopyTo(pixels.AsSpan(y * Width * 4));
        });
        readback.Unmap();

        (byte R, byte G, byte B) At(int x, int y)
        {
            var i = (y * Width + x) * 4;
            return (pixels[i], pixels[i + 1], pixels[i + 2]);
        }

        // Card center: gradient mid-tone (blue-teal), clearly not the gray background.
        var center = At(Width / 2, Height / 2);
        await Assert.That((int)center.B > 80 || (int)center.G > 80).IsTrue();
        // Corner: untouched scene gray (UI is transparent there) — LoadOp.Load worked.
        var corner = At(4, 4);
        await Assert.That(Math.Abs(corner.R - 51)).IsLessThan(6);
        await Assert.That(Math.Abs(corner.G - 51)).IsLessThan(6);
        // Clipped rectangle: inside the ellipse clip it is rose; outside its own bounding box
        // corner the stencil culled it (background gray, not rose).
        // The rect spans x∈[188-60..188+60] roughly; probe its top-left corner region which the
        // elliptical clip excludes.
        var covered = 0;
        var roseInside = false;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var p = At(x, y);
                if (p.R > 200 && p.G < 130 && p.B < 140) roseInside = true;
                if (p.R != 51 || p.G != 51 || p.B != 51) covered++;
            }
        }
        await Assert.That(roseInside).IsTrue();
        await Assert.That(covered).IsGreaterThan(Width * Height / 20);
        await Assert.That(noesisDevice.Unsupported).IsEmpty();

        view.Renderer.Shutdown();
    }

    /// <summary>An immediate-mode surface shaped like the game's map: a dense field of solid
    /// rectangles under a transform, optionally followed by one filled path — the combination the
    /// map renderer draws, and the one that used to lose the whole frame. 1500 rectangles emit
    /// 9000 indices (6 each, an even total); the path's triangle fan adds 3, making the frame's
    /// index block an odd count and so a byte length no multiple of 4.</summary>
    private sealed class DenseFieldSurface : global::Noesis.FrameworkElement
    {
        private readonly global::Noesis.Brush _fieldBrush =
            new global::Noesis.SolidColorBrush(global::Noesis.Color.FromArgb(255, 220, 40, 40));
        private readonly global::Noesis.Brush _pathBrush =
            new global::Noesis.SolidColorBrush(global::Noesis.Color.FromArgb(255, 40, 80, 240));

        public int Columns { get; init; } = 50;
        public int Rows { get; init; } = 30;
        public bool IncludeFilledPath { get; init; } = true;

        protected override void OnRender(global::Noesis.DrawingContext context)
        {
            // The field, drawn under a transform exactly like the map's isometric push.
            var cellWidth = (float)Width / Columns;
            var cellHeight = (float)Height / Rows;
            context.PushTransform(new global::Noesis.MatrixTransform
            {
                Matrix = new global::Noesis.Matrix(1, 0, 0, 1, 0, 0),
            });
            for (var row = 0; row < Rows; row++)
            {
                for (var column = 0; column < Columns; column++)
                {
                    context.DrawRectangle(_fieldBrush, null, new global::Noesis.Rect(
                        column * cellWidth, row * cellHeight, cellWidth, cellHeight));
                }
            }
            context.Pop();

            if (!IncludeFilledPath) return;

            // One filled path on top — a triangle in a known corner, the traveller's robe.
            var figure = new global::Noesis.PathFigure
            {
                StartPoint = new global::Noesis.Point(24, 24),
                IsClosed = true,
                IsFilled = true,
            };
            var points = new global::Noesis.PointCollection
            {
                new global::Noesis.Point(72, 24),
                new global::Noesis.Point(48, 72),
            };
            figure.Segments.Add(new global::Noesis.PolyLineSegment { Points = points });
            var geometry = new global::Noesis.PathGeometry();
            geometry.Figures.Add(figure);
            context.DrawGeometry(_pathBrush, null, geometry);
        }
    }

    /// <summary>Regression for the map-renderer frame corruption (issue #129): a dense rectangle
    /// field plus ONE filled path must render the field exactly as the same frame without the path.
    ///
    /// The mechanism was a silently rejected upload — <c>Queue.WriteBuffer</c> refuses a size that
    /// is not a multiple of 4, and the odd index count contributed by the path made the frame's
    /// whole index block exactly that. So the assertions are three: no validation error was raised
    /// (the direct cause, and invisible without an error scope because nothing pumps Dawn's
    /// uncaptured-error callback), the field pixels are the colour they were drawn in, and the two
    /// frames agree pixel-for-pixel outside the path's own corner.</summary>
    [Test]
    public async Task a_dense_rectangle_field_plus_a_filled_path_renders_without_corrupting_the_frame()
    {
        var device = TryCreateDevice();
        if (device is null)
        {
            Skip.Test("No WebGPU adapter available.");
            return;
        }
        EnsureNoesis();

        var (withPath, unalignedMaps, errorWithPath) = RenderSurface(device, includeFilledPath: true);
        var (withoutPath, _, errorWithoutPath) = RenderSurface(device, includeFilledPath: false);

        // The frame really is the shape that used to break: an odd index count needing padding.
        await Assert.That(unalignedMaps).IsGreaterThan(0);
        await Assert.That(errorWithPath).IsNull();
        await Assert.That(errorWithoutPath).IsNull();

        (byte R, byte G, byte B, byte A) At(byte[] pixels, int x, int y)
        {
            var i = (y * Width + x) * 4;
            return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
        }

        // The field is its own opaque red everywhere the path does not cover it. Before the fix
        // this was the clear colour across the entire frame — the index upload never landed, so
        // every triangle degenerated to index 0.
        var wrong = 0;
        var differing = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (x < PathCorner && y < PathCorner) continue; // the filled path's own bounds
                var a = At(withPath, x, y);
                if (Math.Abs(a.R - 220) > 8 || Math.Abs(a.G - 40) > 8 || Math.Abs(a.B - 40) > 8) wrong++;
                if (a != At(withoutPath, x, y)) differing++;
            }
        }
        await Assert.That(wrong).IsEqualTo(0);

        // The corruption was global, so an exact match against the path-free frame is what pins
        // the bug down: adding the path may change its own corner and nothing else.
        await Assert.That(differing).IsEqualTo(0);

        // ...and the path itself actually landed, so the comparison above is not vacuous.
        var inside = At(withPath, 48, 45);
        await Assert.That((int)inside.B).IsGreaterThan(150);
        await Assert.That((int)inside.R).IsLessThan(120);
    }

    private static (byte[] Pixels, int UnalignedMaps, string? ValidationError) RenderSurface(Device device, bool includeFilledPath)
    {
        var root = new DenseFieldSurface
        {
            IncludeFilledPath = includeFilledPath,
            Width = Width,
            Height = Height,
        };
        var view = global::Noesis.GUI.CreateView(root);
        view.SetSize(Width, Height);

        using var noesisDevice = new NoesisRenderDevice(device, WebGpuSharp.TextureFormat.RGBA8Unorm);
        view.Renderer.Init(noesisDevice);

        var target = device.CreateTexture(new TextureDescriptor
        {
            Label = "NoesisTest.DenseTarget",
            Size = new Extent3D(Width, Height, 1),
            Format = WebGpuSharp.TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = TextureDimension.D2,
        })!;
        var targetView = target.CreateView()!;
        var queue = device.GetQueue()!;

        device.PushErrorScope(ErrorFilter.Validation);
        view.Update(0.0);
        view.Renderer.UpdateRenderTree();

        var encoder = device.CreateCommandEncoder()!;
        var clearColors = new RenderPassColorAttachment[]
        {
            new()
            {
                View = targetView,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearValue = new WebGpuSharp.Color(0.0, 0.0, 0.0, 1.0),
                DepthSlice = null,
            },
        };
        var clearDesc = new RenderPassDescriptor { ColorAttachments = clearColors };
        encoder.BeginRenderPass(in clearDesc).End();

        noesisDevice.BeginFrame(encoder, targetView, Width, Height);
        view.Renderer.RenderOffscreen();
        view.Renderer.Render();
        var unalignedMaps = noesisDevice.UnalignedMaps;
        noesisDevice.EndFrame();

        const uint bpp = 4;
        var padded = (Width * bpp + 255u) & ~255u;
        var readback = device.CreateBuffer(new BufferDescriptor
        {
            Label = "NoesisTest.DenseReadback",
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
        var scope = device.PopErrorScopeSync(5_000_000_000UL);
        var validationError = scope.errorType == ErrorType.NoError ? null : scope.message;

        var pixels = new byte[Width * Height * 4];
        readback.MapSync(MapMode.Read, 0, (nuint)((ulong)padded * Height), 5_000);
        readback.GetConstMappedRange(0, (nuint)((ulong)padded * Height), (ReadOnlySpan<byte> mapped) =>
        {
            for (var y = 0; y < Height; y++)
                mapped.Slice((int)(y * padded), Width * 4).CopyTo(pixels.AsSpan(y * Width * 4));
        });
        readback.Unmap();

        view.Renderer.Shutdown();
        return (pixels, unalignedMaps, validationError);
    }
}
