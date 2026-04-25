using TUnit.Core;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>The content-keyed layout cache dedupes native <c>WgBindGroupLayout</c> instances below
/// the public <see cref="BindGroupLayoutHandle"/> layer. Two <see cref="BindGroupLayoutDesc"/>
/// records with structurally-equal content should produce a single cache entry while each caller
/// receives a distinct handle. Requires a live device (no <c>WgBindGroupLayout</c> public ctor);
/// skips if no GPU adapter is available, matching <see cref="HeadlessSmokeTests"/>.</summary>
public class BindGroupLayoutCacheTests
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

    private static BindGroupLayoutDesc BuildDesc(uint groupIndex, BindGroupLayoutEntryDesc[] entries) =>
        new BindGroupLayoutDesc(groupIndex, entries);

    [Test]
    public async Task duplicate_descriptors_share_the_native_layout()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var entries1 = new[] { new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex | ShaderStage.Fragment, BindingResourceType.UniformBuffer, 16) };
            var entries2 = new[] { new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex | ShaderStage.Fragment, BindingResourceType.UniformBuffer, 16) };

            var h1 = renderer.CreateBindGroupLayout(BuildDesc(0, entries1));
            var h2 = renderer.CreateBindGroupLayout(BuildDesc(0, entries2));

            await Assert.That(h1.IsValid).IsTrue();
            await Assert.That(h2.IsValid).IsTrue();
            // Public handles are distinct — matches the pipeline cache contract.
            await Assert.That(h1).IsNotEqualTo(h2);
            // But the content cache has exactly one entry — structurally-equal descs dedupe.
            await Assert.That(renderer.BindGroupLayoutCacheCountForTest).IsEqualTo(1);

            renderer.DestroyBindGroupLayout(h1);
            renderer.DestroyBindGroupLayout(h2);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task different_group_index_produces_distinct_cache_entries()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var entries = new[] { new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16) };
            var h1 = renderer.CreateBindGroupLayout(BuildDesc(0, entries));
            var h2 = renderer.CreateBindGroupLayout(BuildDesc(1, entries));
            await Assert.That(renderer.BindGroupLayoutCacheCountForTest).IsEqualTo(2);
            renderer.DestroyBindGroupLayout(h1);
            renderer.DestroyBindGroupLayout(h2);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task cache_key_snapshots_entries_so_caller_mutation_is_invisible()
    {
        // Iter-3 regression: the cache key originally captured the descriptor's Entries array
        // by reference, so a caller mutating the array post-insertion could change the key's hash
        // and break dictionary lookups. Iter-3 snapshots the entries into the Key. Mutating the
        // original array between two creates with structurally-equal-at-call-time descriptors
        // must NOT split the cache.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var entries = new[] { new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16) };
            var h1 = renderer.CreateBindGroupLayout(BuildDesc(0, entries));

            // Mutate the array between the two creates. A non-snapshotting Key would now hash
            // differently than the original insertion did, so lookups against the original
            // content would miss; the second create below would make a new cache entry.
            entries[0] = new BindGroupLayoutEntryDesc(7, ShaderStage.Fragment, BindingResourceType.Sampler, 0);

            // Second call provides a fresh array with the ORIGINAL content. Snapshot Key behavior:
            // hits the existing cache entry. Reference Key behavior: misses, makes a second entry.
            var originalAgain = new[] { new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16) };
            var h2 = renderer.CreateBindGroupLayout(BuildDesc(0, originalAgain));

            await Assert.That(h1).IsNotEqualTo(h2);
            await Assert.That(renderer.BindGroupLayoutCacheCountForTest).IsEqualTo(1);

            renderer.DestroyBindGroupLayout(h1);
            renderer.DestroyBindGroupLayout(h2);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task different_entry_visibility_produces_distinct_cache_entries()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var vertEntries = new[] { new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16) };
            var fragEntries = new[] { new BindGroupLayoutEntryDesc(0, ShaderStage.Fragment, BindingResourceType.UniformBuffer, 16) };
            var h1 = renderer.CreateBindGroupLayout(BuildDesc(0, vertEntries));
            var h2 = renderer.CreateBindGroupLayout(BuildDesc(0, fragEntries));
            await Assert.That(renderer.BindGroupLayoutCacheCountForTest).IsEqualTo(2);
            renderer.DestroyBindGroupLayout(h1);
            renderer.DestroyBindGroupLayout(h2);
        }
        finally
        {
            renderer.Dispose();
        }
    }
}
