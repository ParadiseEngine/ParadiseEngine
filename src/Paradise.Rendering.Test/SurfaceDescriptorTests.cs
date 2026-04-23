namespace Paradise.Rendering.Test;

public class SurfaceDescriptorTests
{
    [Test]
    public async Task all_platform_variants_round_trip_their_fields()
    {
        foreach (var platform in Enum.GetValues<SurfacePlatform>())
        {
            var desc = new SurfaceDescriptor(
                platform,
                DisplayHandle: new IntPtr(0xCAFE),
                WindowHandle: new IntPtr(0xBEEF),
                Width: 1920,
                Height: 1080);

            await Assert.That(desc.Platform).IsEqualTo(platform);
            await Assert.That(desc.DisplayHandle).IsEqualTo(new IntPtr(0xCAFE));
            await Assert.That(desc.WindowHandle).IsEqualTo(new IntPtr(0xBEEF));
            await Assert.That(desc.Width).IsEqualTo(1920u);
            await Assert.That(desc.Height).IsEqualTo(1080u);
        }
    }

    [Test]
    public async Task headless_factory_zero_handles_and_headless_platform()
    {
        var desc = SurfaceDescriptor.Headless(800, 600);
        await Assert.That(desc.Platform).IsEqualTo(SurfacePlatform.Headless);
        await Assert.That(desc.DisplayHandle).IsEqualTo(IntPtr.Zero);
        await Assert.That(desc.WindowHandle).IsEqualTo(IntPtr.Zero);
        await Assert.That(desc.Width).IsEqualTo(800u);
        await Assert.That(desc.Height).IsEqualTo(600u);
    }

    [Test]
    public async Task equality_matches_field_by_field()
    {
        var a = new SurfaceDescriptor(SurfacePlatform.Wayland, new IntPtr(1), new IntPtr(2), 100, 100);
        var b = new SurfaceDescriptor(SurfacePlatform.Wayland, new IntPtr(1), new IntPtr(2), 100, 100);
        var c = new SurfaceDescriptor(SurfacePlatform.Xlib, new IntPtr(1), new IntPtr(2), 100, 100);
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a == b).IsTrue();
        await Assert.That(a == c).IsFalse();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }
}
