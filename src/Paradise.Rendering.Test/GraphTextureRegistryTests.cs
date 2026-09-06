using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Test;

/// <summary>The registry's one promise: the same declaration costs nothing, a changed one
/// replaces exactly that target, and nothing it minted outlives it.</summary>
public class GraphTextureRegistryTests
{
    private static TextureDesc Target(uint width, uint height, uint layers = 1) => new(
        "ignored", width, height, layers, 1, 1, TextureDimension.D2, TextureFormat.Rgba16Float,
        TextureUsage.RenderAttachment | TextureUsage.TextureBinding);

    [Test]
    public async Task the_same_declaration_twice_creates_once_and_keeps_the_handle()
    {
        var factory = new FakeTextureFactory();
        using var registry = new GraphTextureRegistry(factory);

        var first = registry.Ensure("hdr", Target(64, 64));
        var texture = registry.Texture("hdr");
        var view = registry.View("hdr");
        var second = registry.Ensure("hdr", Target(64, 64));

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(registry.Texture("hdr")).IsEqualTo(texture);
        await Assert.That(registry.View("hdr")).IsEqualTo(view);
        await Assert.That(factory.TexturesCreated).IsEqualTo(1);
        await Assert.That(factory.ViewsCreated).IsEqualTo(1);
    }

    [Test]
    public async Task a_changed_shape_replaces_the_texture_and_every_view_over_it()
    {
        var factory = new FakeTextureFactory();
        using var registry = new GraphTextureRegistry(factory);
        registry.Ensure("hdr", Target(64, 64));
        var oldTexture = registry.Texture("hdr");
        var oldView = registry.View("hdr");

        var changed = registry.Ensure("hdr", Target(128, 128));
        var newTexture = registry.Texture("hdr");
        var newView = registry.View("hdr");

        await Assert.That(changed).IsTrue();
        await Assert.That(newTexture).IsNotEqualTo(oldTexture);
        await Assert.That(newView).IsNotEqualTo(oldView);
        await Assert.That(factory.Textures.ContainsKey(oldTexture)).IsFalse();
        await Assert.That(factory.Views.ContainsKey(oldView)).IsFalse();
        await Assert.That(factory.Views[newView].Texture).IsEqualTo(newTexture);
    }

    [Test]
    public async Task the_registry_name_becomes_the_texture_label()
    {
        var factory = new FakeTextureFactory();
        using var registry = new GraphTextureRegistry(factory);
        registry.Ensure("PbrHdrScene", Target(8, 8));

        var name = factory.Textures[registry.Texture("PbrHdrScene")].Name;
        await Assert.That(name).IsEqualTo("PbrHdrScene");
    }

    [Test]
    public async Task layer_views_are_one_per_layer_and_the_array_view_spans_them_all()
    {
        var factory = new FakeTextureFactory();
        using var registry = new GraphTextureRegistry(factory);
        registry.Ensure("shadows", Target(16, 16, layers: 3));

        var layer1 = registry.SliceView("shadows", 1);
        var layer1Again = registry.SliceView("shadows", 1);
        var array = registry.ArrayView("shadows");
        var layer1Desc = factory.Views[layer1];
        var arrayDesc = factory.Views[array];

        await Assert.That(layer1Again).IsEqualTo(layer1);
        await Assert.That(layer1Desc.Dimension).IsEqualTo(TextureViewDimension.D2);
        await Assert.That(layer1Desc.BaseArrayLayer).IsEqualTo(1u);
        await Assert.That(arrayDesc.Dimension).IsEqualTo(TextureViewDimension.D2Array);
        await Assert.That(arrayDesc.ArrayLayerCount).IsEqualTo(3u);
        await Assert.That(factory.ViewsCreated).IsEqualTo(2);
    }

    /// <summary>A shader that declares an array texture samples a D2Array view, even over one layer.</summary>
    [Test]
    public async Task a_single_layer_array_still_gets_an_array_view()
    {
        var factory = new FakeTextureFactory();
        using var registry = new GraphTextureRegistry(factory);
        registry.Ensure("shadows", Target(16, 16, layers: 1));

        var dimension = factory.Views[registry.ArrayView("shadows")].Dimension;
        await Assert.That(dimension).IsEqualTo(TextureViewDimension.D2Array);
    }

    [Test]
    public async Task a_layer_past_the_end_is_refused()
    {
        var factory = new FakeTextureFactory();
        using var registry = new GraphTextureRegistry(factory);
        registry.Ensure("shadows", Target(16, 16, layers: 2));

        await Assert.That(() => registry.SliceView("shadows", 2)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task release_destroys_the_target_and_forgets_it()
    {
        var factory = new FakeTextureFactory();
        using var registry = new GraphTextureRegistry(factory);
        registry.Ensure("scene", Target(8, 8));
        registry.View("scene");

        registry.Release("scene");
        registry.Release("scene");

        await Assert.That(registry.Contains("scene")).IsFalse();
        await Assert.That(factory.Textures.Count).IsEqualTo(0);
        await Assert.That(factory.Views.Count).IsEqualTo(0);
    }

    [Test]
    public async Task export_survives_recreation_because_the_outside_reader_asks_by_name()
    {
        var factory = new FakeTextureFactory();
        using var registry = new GraphTextureRegistry(factory);
        registry.Ensure("scene", Target(8, 8));
        registry.Export("scene");

        registry.Ensure("scene", Target(16, 16));

        await Assert.That(registry.IsExported("scene")).IsTrue();
    }

    [Test]
    public async Task an_undeclared_name_is_an_error_that_names_it()
    {
        var factory = new FakeTextureFactory();
        using var registry = new GraphTextureRegistry(factory);

        await Assert.That(() => registry.Texture("nothing")).Throws<KeyNotFoundException>()
            .WithMessageContaining("nothing");
    }

    [Test]
    public async Task dispose_returns_everything_to_the_factory()
    {
        var factory = new FakeTextureFactory();
        var registry = new GraphTextureRegistry(factory);
        registry.Ensure("a", Target(8, 8));
        registry.Ensure("b", Target(8, 8, layers: 2));
        registry.View("a");
        registry.SliceView("b", 1);
        registry.ArrayView("b");

        registry.Dispose();

        await Assert.That(factory.Textures.Count).IsEqualTo(0);
        await Assert.That(factory.Views.Count).IsEqualTo(0);
    }
}
