using Paradise.Rendering.WebGPU.Internal;
using Paradise.Windowing;
using WebGpuSharp;

namespace Paradise.Rendering.WebGPU.Test;

public sealed class AndroidSurfaceTests
{
    [Test]
    public async Task AndroidSourceRejectsNullWindow()
    {
        await Assert.That(() => CreateSource(nint.Zero)).Throws<ArgumentException>();
    }

    [Test]
    public async Task AndroidSourceSetsNativeHandleAndChainType()
    {
        var actual = DescribeSource((nint)1234);
        await Assert.That(actual.Window).IsEqualTo((nint)1234);
        await Assert.That(actual.Type).IsEqualTo(SType.SurfaceSourceAndroidNativeWindow);
        await Assert.That(actual.Next).IsEqualTo(nint.Zero);
    }

    [Test]
    [Arguments(SurfacePlatform.Unknown, 0)]
    [Arguments(SurfacePlatform.Win32, 1)]
    [Arguments(SurfacePlatform.Xlib, 2)]
    [Arguments(SurfacePlatform.Wayland, 3)]
    [Arguments(SurfacePlatform.Cocoa, 4)]
    [Arguments(SurfacePlatform.Headless, 5)]
    [Arguments(SurfacePlatform.Android, 6)]
    public async Task AddingAndroidPreservesExistingSurfaceValues(SurfacePlatform platform, int expected)
    {
        await Assert.That((int)platform).IsEqualTo(expected);
    }

    private static void CreateSource(nint window) => SurfaceFactory.CreateAndroidSource(window);

    private static unsafe (nint Window, SType Type, nint Next) DescribeSource(nint window)
    {
        var source = SurfaceFactory.CreateAndroidSource(window);
        return ((nint)source.Window, source.Chain.SType, (nint)source.Chain.Next);
    }
}
