using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Test;

public class BindGroupCacheTests
{
    private static readonly BindGroupLayoutDesc Layout = new(0, []);
    private static readonly BindGroupLayoutDesc OtherLayout = new(0, []);

    private static BindGroupEntryDesc View(uint binding, uint view) =>
        BindGroupEntryDesc.ForTextureView(binding, new TextureViewHandle(view, 1));

    [Test]
    public async Task the_same_content_is_the_same_group()
    {
        var factory = new FakeBindGroupFactory();
        using var cache = new BindGroupCache(factory);

        var first = cache.Get("g", Layout, [View(0, 7), BindGroupEntryDesc.ForSampler(1, new SamplerHandle(3, 1))]);
        var second = cache.Get("g", Layout, [View(0, 7), BindGroupEntryDesc.ForSampler(1, new SamplerHandle(3, 1))]);

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(factory.Created).IsEqualTo(1);
    }

    [Test]
    public async Task a_different_view_or_layout_is_a_different_group()
    {
        var factory = new FakeBindGroupFactory();
        using var cache = new BindGroupCache(factory);

        var a = cache.Get("g", Layout, [View(0, 7)]);
        var b = cache.Get("g", Layout, [View(0, 8)]);
        var c = cache.Get("g", OtherLayout, [View(0, 7)]);

        await Assert.That(b).IsNotEqualTo(a);
        await Assert.That(c).IsNotEqualTo(a);
        await Assert.That(factory.Created).IsEqualTo(3);
    }

    /// <summary>What happens to a culled feature's groups, and to groups over a view a resize
    /// retired: nobody asks, and after a few frames they are gone.</summary>
    [Test]
    public async Task a_group_nobody_asks_for_ages_out()
    {
        var factory = new FakeBindGroupFactory();
        using var cache = new BindGroupCache(factory);
        var kept = cache.Get("kept", Layout, [View(0, 1)]);
        var dropped = cache.Get("dropped", Layout, [View(0, 2)]);

        for (var frame = 0; frame < 5; frame++)
        {
            cache.Get("kept", Layout, [View(0, 1)]);
            cache.EndFrame();
        }

        await Assert.That(factory.Groups.ContainsKey(kept)).IsTrue();
        await Assert.That(factory.Groups.ContainsKey(dropped)).IsFalse();
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task a_group_asked_for_every_few_frames_is_never_recreated()
    {
        var factory = new FakeBindGroupFactory();
        using var cache = new BindGroupCache(factory);

        for (var frame = 0; frame < 12; frame++)
        {
            if (frame % 3 == 0) cache.Get("g", Layout, [View(0, 1)]);
            cache.EndFrame();
        }

        await Assert.That(factory.Created).IsEqualTo(1);
    }

    [Test]
    public async Task dispose_destroys_every_group()
    {
        var factory = new FakeBindGroupFactory();
        var cache = new BindGroupCache(factory);
        cache.Get("a", Layout, [View(0, 1)]);
        cache.Get("b", Layout, [View(0, 2)]);

        cache.Dispose();

        await Assert.That(factory.Groups.Count).IsEqualTo(0);
    }
}
