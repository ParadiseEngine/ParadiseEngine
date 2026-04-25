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
    public async Task create_texture_rejects_combined_depth_stencil_format()
    {
        // Iter-10 fix: Depth24PlusStencil8 requires stencil load/store/clear authoring on
        // DepthAttachmentDesc that the M2 surface doesn't expose. Reject at create time so a
        // pipeline-less or pre-pipeline pass cannot reach BeginPass with a combined-format
        // depth texture and trip Dawn's stencil-ops-required validation.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var desc = new TextureDesc(
                Name: "BadCombined",
                Width: 16, Height: 16,
                DepthOrArrayLayers: 1, MipLevelCount: 1, SampleCount: 1,
                Dimension: TextureDimension.D2,
                Format: TextureFormat.Depth24PlusStencil8,
                Usage: TextureUsage.RenderAttachment);
            await Assert.That(() => renderer.CreateTexture(in desc)).Throws<NotSupportedException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task create_texture_rejects_non_d2_dimension()
    {
        // Iter-11 fix: M2's SampledTexture binding-layout build hardcodes ViewDimension=D2.
        // Reject non-D2 textures at parent-creation so the TextureViewDimension.Undefined
        // inheritance path can't smuggle a layered/3D texture through into the layout build.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var desc = new TextureDesc(
                Name: "Bad3D",
                Width: 16, Height: 16,
                DepthOrArrayLayers: 4, MipLevelCount: 1, SampleCount: 1,
                Dimension: TextureDimension.D3,
                Format: TextureFormat.Rgba8Unorm,
                Usage: TextureUsage.TextureBinding);
            await Assert.That(() => renderer.CreateTexture(in desc)).Throws<NotSupportedException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task create_texture_rejects_layered_d2()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var desc = new TextureDesc(
                Name: "BadLayered",
                Width: 16, Height: 16,
                DepthOrArrayLayers: 2, MipLevelCount: 1, SampleCount: 1,
                Dimension: TextureDimension.D2,
                Format: TextureFormat.Rgba8Unorm,
                Usage: TextureUsage.TextureBinding);
            await Assert.That(() => renderer.CreateTexture(in desc)).Throws<NotSupportedException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task create_texture_with_data_rejects_byte_volume_mismatch()
    {
        // Iter-13 fix: typed CreateTextureWithData<T> requires pixels.Length * sizeof(T) ==
        // width * height * bpp exactly. A non-byte caller for an R8Unorm texture would otherwise
        // mispack rows because the row-stride math derives BytesPerRow from bpp not sizeof(T).
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var desc = new TextureDesc(
                Name: "BadByteVolume",
                Width: 4, Height: 4,
                DepthOrArrayLayers: 1, MipLevelCount: 1, SampleCount: 1,
                Dimension: TextureDimension.D2,
                Format: TextureFormat.R8Unorm,
                Usage: TextureUsage.TextureBinding);
            // R8Unorm 4x4 = 16 bytes expected; passing 16 ints (= 64 bytes) is a 4x mismatch.
            var ints = new int[16];
            await Assert.That(() => renderer.CreateTextureWithData(in desc, (ReadOnlySpan<int>)ints))
                .Throws<ArgumentException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task create_bind_group_rejects_duplicate_binding_across_spans()
    {
        // Iter-9 fix: WebGPU requires unique binding numbers per bind group. The buffer/texture/
        // sampler split in BindGroupDesc could let a caller author two entries at the same
        // binding number across spans; the engine rejects with a descriptive message before
        // hitting Dawn's opaque diagnostic.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var layout = renderer.CreateBindGroupLayout(new BindGroupLayoutDesc(0, new[]
            {
                new BindGroupLayoutEntryDesc(0, ShaderStage.Fragment, BindingResourceType.SampledTexture, 0),
                new BindGroupLayoutEntryDesc(1, ShaderStage.Fragment, BindingResourceType.Sampler, 0),
            }));
            var tex = renderer.CreateTexture(new TextureDesc(
                Name: "Probe", Width: 2, Height: 2, DepthOrArrayLayers: 1, MipLevelCount: 1, SampleCount: 1,
                Dimension: TextureDimension.D2, Format: TextureFormat.Rgba8Unorm,
                Usage: TextureUsage.TextureBinding));
            var view = renderer.CreateTextureView(tex, new RenderViewDesc(
                Name: null, Format: TextureFormat.Rgba8Unorm, Dimension: TextureViewDimension.D2,
                Aspect: TextureAspect.All, BaseMipLevel: 0, MipLevelCount: 1, BaseArrayLayer: 0, ArrayLayerCount: 1));
            var sampler = renderer.CreateSampler(new SamplerDesc(
                Name: null,
                AddressU: SamplerAddressMode.ClampToEdge, AddressV: SamplerAddressMode.ClampToEdge, AddressW: SamplerAddressMode.ClampToEdge,
                MagFilter: SamplerFilterMode.Nearest, MinFilter: SamplerFilterMode.Nearest, MipmapFilter: SamplerFilterMode.Nearest));

            // Both texture and sampler at binding 0 — duplicate across spans.
            var desc = new BindGroupDesc
            {
                Name = "DupBindGroup",
                Layout = layout,
                Textures = new[] { new BindGroupTextureEntry(0, view) },
                Samplers = new[] { new BindGroupSamplerEntry(0, sampler) },
            };
            await Assert.That(() => renderer.CreateBindGroup(in desc)).Throws<InvalidOperationException>();

            renderer.DestroySampler(sampler);
            renderer.DestroyTextureView(view);
            renderer.DestroyTexture(tex);
            renderer.DestroyBindGroupLayout(layout);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task create_bind_group_layout_rejects_duplicate_binding()
    {
        // Iter-12 fix: symmetric with CreateBindGroup uniqueness — a layout with two entries at
        // the same binding number is rejected before reaching Dawn.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var dupLayout = new BindGroupLayoutDesc(0, new[]
            {
                new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16),
                new BindGroupLayoutEntryDesc(0, ShaderStage.Fragment, BindingResourceType.Sampler, 0),
            });
            await Assert.That(() => renderer.CreateBindGroupLayout(dupLayout)).Throws<InvalidOperationException>();
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
