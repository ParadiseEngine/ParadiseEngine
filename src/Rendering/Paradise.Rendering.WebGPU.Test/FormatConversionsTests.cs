using System;
using Paradise.Rendering.WebGPU.Internal;
using WgTextureFormat = WebGpuSharp.TextureFormat;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Coverage for the <see cref="FormatConversions"/> helpers' error-path symmetry.
/// Iter-7 fix for OpenCara's iter-6 minor finding: <c>FromWgpu(TextureFormat)</c> used to silently
/// map unknown values to <c>TextureFormat.Undefined</c> while every sibling <c>ToWgpu</c> overload
/// threw <c>NotSupportedException</c>. The asymmetry was a trap — a swapchain format outside our
/// explicit map (e.g. HDR <c>RGBA16Float</c>, or a future WebGPUSharp value) would surface as an
/// opaque Dawn pipeline rejection instead of a descriptive exception at the conversion site.</summary>
public class FormatConversionsTests
{
    [Test]
    public async Task from_wgpu_throws_not_supported_for_unmapped_value()
    {
        // Cast an arbitrary integer not in our mapping table to force the fallthrough arm. Use a
        // value far above any mapped enum member so the test stays valid even if Paradise.Rendering
        // adds more formats in a later milestone.
        var unmapped = (WgTextureFormat)0xBEEF;
        await Assert.That(() => FormatConversions.FromWgpu(unmapped)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task from_wgpu_round_trips_the_mapped_bgra8unorm_value()
    {
        // Sanity: the happy-path mappings are unchanged. Bgra8Unorm is the M1 sample's swapchain
        // format and the iter-6 finding was specifically about ColorFormat calling FromWgpu on
        // that path.
        await Assert.That(FormatConversions.FromWgpu(WgTextureFormat.BGRA8Unorm)).IsEqualTo(TextureFormat.Bgra8Unorm);
        await Assert.That(FormatConversions.ToWgpu(TextureFormat.Bgra8Unorm)).IsEqualTo(WgTextureFormat.BGRA8Unorm);
    }

    [Test]
    public async Task from_wgpu_maps_undefined_symmetrically()
    {
        // The single "quiet" mapping that survives — WgTextureFormat.Undefined is the explicit
        // sentinel on both sides and must stay round-trippable.
        await Assert.That(FormatConversions.FromWgpu(WgTextureFormat.Undefined)).IsEqualTo(TextureFormat.Undefined);
        await Assert.That(FormatConversions.ToWgpu(TextureFormat.Undefined)).IsEqualTo(WgTextureFormat.Undefined);
    }
}
