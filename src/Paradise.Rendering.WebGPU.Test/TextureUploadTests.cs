using TUnit.Core;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Smoke tests for the M2 texture create + upload + view + sampler surface. Requires a
/// live Dawn device; skips when no GPU adapter is available. We don't attempt a full round-trip
/// readback (that requires copying the texture back into a mappable staging buffer and awaiting
/// MapAsync, which the M2 backend does not expose) — the assertion here is that
/// CreateTextureWithData succeeds without Dawn validation errors on a 2x2 RGBA8 upload, plus that
/// a view and sampler can be created and a bind group resolved against them.</summary>
public class TextureUploadTests
{
    private static WebGpuRenderer? TryCreateHeadlessOrSkip()
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(16, 16);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter available on this host: {ex.Message}");
            return null;
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native library not loadable on this host: {ex.Message}");
            return null;
        }
    }

    [Test]
    public async Task create_texture_with_data_round_trips_through_view_and_sampler()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var pixels = new byte[]
            {
                255, 0, 0, 255,   0, 255, 0, 255,
                0, 0, 255, 255,   255, 255, 255, 255,
            };
            var texDesc = new TextureDesc(
                Name: "RoundTripTex",
                Width: 2,
                Height: 2,
                DepthOrArrayLayers: 1,
                MipLevelCount: 1,
                SampleCount: 1,
                Dimension: TextureDimension.D2,
                Format: TextureFormat.Rgba8Unorm,
                Usage: TextureUsage.TextureBinding);
            var texture = renderer.CreateTextureWithData(in texDesc, (ReadOnlySpan<byte>)pixels);
            await Assert.That(texture.IsValid).IsTrue();

            var view = renderer.CreateTextureView(texture, new RenderViewDesc(
                Name: "RoundTripView",
                Format: TextureFormat.Rgba8Unorm,
                Dimension: TextureViewDimension.D2,
                Aspect: TextureAspect.All,
                BaseMipLevel: 0,
                MipLevelCount: 1,
                BaseArrayLayer: 0,
                ArrayLayerCount: 1));
            await Assert.That(view.IsValid).IsTrue();

            var sampler = renderer.CreateSampler(new SamplerDesc(
                Name: "RoundTripSampler",
                AddressU: SamplerAddressMode.ClampToEdge,
                AddressV: SamplerAddressMode.ClampToEdge,
                AddressW: SamplerAddressMode.ClampToEdge,
                MagFilter: SamplerFilterMode.Nearest,
                MinFilter: SamplerFilterMode.Nearest,
                MipmapFilter: SamplerFilterMode.Nearest));
            await Assert.That(sampler.IsValid).IsTrue();

            var layout = renderer.CreateBindGroupLayout(new BindGroupLayoutDesc(0, new[]
            {
                new BindGroupLayoutEntryDesc(0, ShaderStage.Fragment, BindingResourceType.SampledTexture, 0),
                new BindGroupLayoutEntryDesc(1, ShaderStage.Fragment, BindingResourceType.Sampler, 0),
            }));
            var bindGroup = renderer.CreateBindGroup(new BindGroupDesc
            {
                Name = "RoundTripBindGroup",
                Layout = layout,
                Textures = new[] { new BindGroupTextureEntry(0, view) },
                Samplers = new[] { new BindGroupSamplerEntry(1, sampler) },
            });
            await Assert.That(bindGroup.IsValid).IsTrue();

            renderer.DestroyBindGroup(bindGroup);
            renderer.DestroyBindGroupLayout(layout);
            renderer.DestroySampler(sampler);
            renderer.DestroyTextureView(view);
            renderer.DestroyTexture(texture);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task create_texture_with_data_does_not_leak_on_unsupported_format()
    {
        // Iter-3 regression: CreateTextureWithData previously allocated the texture BEFORE
        // computing bytes-per-pixel, so an unsupported format threw NotSupportedException with
        // the slot-table entry + native already in flight. Iter-3 resolves bpp first; this test
        // pins the no-leak invariant by repeating the failing call N times and asserting the
        // slot-table count holds steady at the baseline.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            // Baseline measured AFTER one warm valid create+destroy so the slot pool is stable.
            var warmDesc = new TextureDesc(
                Name: "WarmRgba",
                Width: 2, Height: 2,
                DepthOrArrayLayers: 1, MipLevelCount: 1, SampleCount: 1,
                Dimension: TextureDimension.D2,
                Format: TextureFormat.Rgba8Unorm,
                Usage: TextureUsage.TextureBinding);
            var warmPixels = new byte[16];
            var warm = renderer.CreateTextureWithData(in warmDesc, (ReadOnlySpan<byte>)warmPixels);
            renderer.DestroyTexture(warm);
            var baseline = renderer.TextureSlotCountForTest;

            // Depth32Float is in BytesPerPixel's not-supported set — CreateTextureWithData throws
            // before allocating now (post-iter-3). Pre-iter-3, each iteration leaked one slot.
            var badDesc = new TextureDesc(
                Name: "BadFormat",
                Width: 2, Height: 2,
                DepthOrArrayLayers: 1, MipLevelCount: 1, SampleCount: 1,
                Dimension: TextureDimension.D2,
                Format: TextureFormat.Depth32Float,
                Usage: TextureUsage.TextureBinding);
            var emptyPixels = new byte[16];
            for (var i = 0; i < 4; i++)
            {
                try { _ = renderer.CreateTextureWithData(in badDesc, (ReadOnlySpan<byte>)emptyPixels); }
                catch (NotSupportedException) { /* expected */ }
            }

            await Assert.That(renderer.TextureSlotCountForTest).IsEqualTo(baseline);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task write_buffer_accepts_repeated_uniform_updates()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var buffer = renderer.CreateBuffer(new BufferDesc(
                Name: "UniformScratch",
                Size: 16,
                Usage: BufferUsage.Uniform | BufferUsage.CopyDst));
            Span<float> value = stackalloc float[1];
            for (var i = 0; i < 5; i++)
            {
                value[0] = i * 0.25f;
                renderer.WriteBuffer(buffer, 0, (ReadOnlySpan<float>)value);
            }
            renderer.DestroyBuffer(buffer);
        }
        finally
        {
            renderer.Dispose();
        }
    }
}
