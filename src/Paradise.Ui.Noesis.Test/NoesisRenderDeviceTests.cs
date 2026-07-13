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
}
