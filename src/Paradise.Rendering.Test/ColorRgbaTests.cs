namespace Paradise.Rendering.Test;

public class ColorRgbaTests
{
    [Test]
    public async Task black_is_opaque_zero_rgb()
    {
        var c = ColorRgba.Black;
        await Assert.That(c.R).IsEqualTo(0f);
        await Assert.That(c.G).IsEqualTo(0f);
        await Assert.That(c.B).IsEqualTo(0f);
        await Assert.That(c.A).IsEqualTo(1f);
    }

    [Test]
    public async Task white_is_opaque_one_rgb()
    {
        var c = ColorRgba.White;
        await Assert.That(c.R).IsEqualTo(1f);
        await Assert.That(c.G).IsEqualTo(1f);
        await Assert.That(c.B).IsEqualTo(1f);
        await Assert.That(c.A).IsEqualTo(1f);
    }

    [Test]
    public async Task transparent_is_zero_alpha()
    {
        await Assert.That(ColorRgba.Transparent.A).IsEqualTo(0f);
    }

    [Test]
    public async Task cornflower_blue_matches_xna_classic_constant()
    {
        var c = ColorRgba.CornflowerBlue;
        await Assert.That(c.R).IsEqualTo(0.392f);
        await Assert.That(c.G).IsEqualTo(0.584f);
        await Assert.That(c.B).IsEqualTo(0.929f);
        await Assert.That(c.A).IsEqualTo(1f);
    }

    [Test]
    public async Task color_equality_is_componentwise()
    {
        var a = new ColorRgba(0.1f, 0.2f, 0.3f, 0.4f);
        var b = new ColorRgba(0.1f, 0.2f, 0.3f, 0.4f);
        var c = new ColorRgba(0.1f, 0.2f, 0.3f, 0.5f);
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != c).IsTrue();
    }
}
