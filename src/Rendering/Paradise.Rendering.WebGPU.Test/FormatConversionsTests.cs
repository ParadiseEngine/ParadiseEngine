using System;
using Paradise.Rendering.WebGPU.Internal;
using WgTextureFormat = WebGpuSharp.TextureFormat;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Checks explicit texture-format mappings and rejection of unmapped values.</summary>
public class FormatConversionsTests
{
    [Test]
    public async Task from_wgpu_throws_not_supported_for_unmapped_value()
    {
        // Keep an unknown value distinct from Undefined, which is a supported sentinel.
        var unmapped = (WgTextureFormat)0xBEEF;
        await Assert.That(() => FormatConversions.FromWgpu(unmapped)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task to_wgpu_throws_not_supported_for_unmapped_value()
    {
        var unmapped = (TextureFormat)0xBEEF;
        await Assert.That(() => FormatConversions.ToWgpu(unmapped)).Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(WgTextureFormat.Undefined, TextureFormat.Undefined)]
    [Arguments(WgTextureFormat.R8Unorm, TextureFormat.R8Unorm)]
    [Arguments(WgTextureFormat.RGBA8Unorm, TextureFormat.Rgba8Unorm)]
    [Arguments(WgTextureFormat.RGBA8UnormSrgb, TextureFormat.Rgba8UnormSrgb)]
    [Arguments(WgTextureFormat.BGRA8Unorm, TextureFormat.Bgra8Unorm)]
    [Arguments(WgTextureFormat.BGRA8UnormSrgb, TextureFormat.Bgra8UnormSrgb)]
    [Arguments(WgTextureFormat.Depth32Float, TextureFormat.Depth32Float)]
    [Arguments(WgTextureFormat.Depth24PlusStencil8, TextureFormat.Depth24PlusStencil8)]
    public async Task reverse_mapped_texture_formats_round_trip(WgTextureFormat native, TextureFormat engine)
    {
        await Assert.That(FormatConversions.FromWgpu(native)).IsEqualTo(engine);
        await Assert.That(FormatConversions.ToWgpu(engine)).IsEqualTo(native);
    }
}
