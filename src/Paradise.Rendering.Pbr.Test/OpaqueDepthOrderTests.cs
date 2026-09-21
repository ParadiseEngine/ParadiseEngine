using System.Numerics;

namespace Paradise.Rendering.Pbr.Test;

public sealed class OpaqueDepthOrderTests
{
    [Test]
    public async Task SortIsStableNearToFarAndNeverCrossesMaskedOrderingBarriers()
    {
        var renderer = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(renderer, ShaderPrograms.Load("Shaders.pbr"));
        var opaque = materials.AddMaterial(new PbrMaterialDesc());
        var masked = materials.AddMaterial(new PbrMaterialDesc { AlphaMode = PbrAlphaMode.Mask });
        var frame = new PbrFrameData();
        FrameDraw Draw(int id, int material, float depth)
        {
            var primitive = new PbrPrimitive(default, default, 3, 0, 0, material);
            return new FrameDraw(new PbrInstance { Mesh = new PbrMesh([primitive]) }, primitive, depth, id);
        }
        frame.Opaque.AddRange([Draw(0, opaque, -10), Draw(1, opaque, -2), Draw(2, opaque, -2),
            Draw(3, masked, -20), Draw(4, opaque, -8), Draw(5, opaque, -1)]);
        frame.Blend.Add(Draw(6, opaque, -100));
        frame.SortOpaqueFrontToBack(materials);
        await Assert.That(frame.Opaque.Select(draw => draw.ObjectIndex).ToArray()).IsEquivalentTo([1, 2, 0, 3, 5, 4]);
        await Assert.That(frame.Opaque[0].ObjectIndex).IsEqualTo(1);
        await Assert.That(frame.Opaque[1].ObjectIndex).IsEqualTo(2);
        await Assert.That(frame.Opaque[3].ObjectIndex).IsEqualTo(3);
        await Assert.That(frame.Blend[0].ObjectIndex).IsEqualTo(6);
        var once = frame.Opaque.ToArray();
        frame.SortOpaqueFrontToBack(materials);
        await Assert.That(frame.Opaque.SequenceEqual(once)).IsTrue();
    }

    [Test]
    public async Task OrderingRemainsOptIn()
    {
        await Assert.That(new PbrInstancing().SortOpaqueFrontToBack).IsFalse();
        await Assert.That(new PbrInstancing().PackAndRegroup).IsFalse();
    }
}
