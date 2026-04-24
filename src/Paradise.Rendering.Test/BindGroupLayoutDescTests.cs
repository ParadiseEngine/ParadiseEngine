namespace Paradise.Rendering.Test;

/// <summary>Records in <see cref="BindGroupLayoutDesc"/> and <see cref="BindGroupLayoutEntryDesc"/>
/// need stable structural identity so the WebGPU backend's content-keyed
/// <see cref="BindGroupLayoutCache"/> (in the internal <c>Paradise.Rendering.WebGPU.Internal</c>
/// namespace) can dedupe identical layouts minted by two independent loader calls. The records
/// themselves are plain sealed records — these tests lock their value semantics before the cache
/// tries to key on them.</summary>
public class BindGroupLayoutDescTests
{
    private static BindGroupLayoutDesc BuildSample(uint groupIndex = 0)
    {
        return new BindGroupLayoutDesc(
            GroupIndex: groupIndex,
            Entries: new[]
            {
                new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex | ShaderStage.Fragment, BindingResourceType.UniformBuffer, 16),
                new BindGroupLayoutEntryDesc(1, ShaderStage.Fragment, BindingResourceType.SampledTexture, 0),
                new BindGroupLayoutEntryDesc(2, ShaderStage.Fragment, BindingResourceType.Sampler, 0),
            });
    }

    [Test]
    public async Task entry_equality_is_structural()
    {
        var a = new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16);
        var b = new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16);
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task entry_inequality_detected_on_each_field()
    {
        var baseEntry = new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16);
        await Assert.That(baseEntry).IsNotEqualTo(baseEntry with { Binding = 1 });
        await Assert.That(baseEntry).IsNotEqualTo(baseEntry with { Visibility = ShaderStage.Fragment });
        await Assert.That(baseEntry).IsNotEqualTo(baseEntry with { Type = BindingResourceType.Sampler });
        await Assert.That(baseEntry).IsNotEqualTo(baseEntry with { MinBufferSize = 32 });
    }

    [Test]
    public async Task layout_entries_array_is_reference_compared_by_record_synthesis()
    {
        // Record-struct synthesis uses reference equality on T[] properties. This is the *design*
        // for the raw record; structural equality across distinct allocations is provided by the
        // backend's BindGroupLayoutCache (WebGPU internal) which walks entries element-by-element.
        // The test locks the raw semantic so a reader doesn't expect value-equality on the arrays.
        var entries1 = new[] { new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16) };
        var entries2 = new[] { new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16) };
        var a = new BindGroupLayoutDesc(0, entries1);
        var b = new BindGroupLayoutDesc(0, entries2);
        await Assert.That(ReferenceEquals(entries1, entries2)).IsFalse();
        await Assert.That(a).IsNotEqualTo(b); // Record-synthesized Equals compares array references.

        var c = new BindGroupLayoutDesc(0, entries1);
        await Assert.That(a).IsEqualTo(c); // Same array reference → equal.
    }

    [Test]
    public async Task sample_layout_has_expected_entry_count_and_visibilities()
    {
        var l = BuildSample(0);
        await Assert.That(l.Entries.Length).IsEqualTo(3);
        await Assert.That(l.Entries[0].Type).IsEqualTo(BindingResourceType.UniformBuffer);
        await Assert.That(l.Entries[0].Visibility).IsEqualTo(ShaderStage.Vertex | ShaderStage.Fragment);
        await Assert.That(l.Entries[1].Type).IsEqualTo(BindingResourceType.SampledTexture);
        await Assert.That(l.Entries[2].Type).IsEqualTo(BindingResourceType.Sampler);
    }
}
