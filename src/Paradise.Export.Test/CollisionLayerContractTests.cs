using Paradise.Export.Geometry;

namespace Paradise.Export.Tests;

// The collision-layer contract: a Godot collision_layer mask collapses to the Unity-style single
// layer INDEX that ColliderShapeData.Layer carries (consumers reconstruct 1u << Layer). Guards the
// mask→index decision the Godot exporter (SceneDataExporter.ResolveLayerIndex) can't unit-test
// directly because it lives behind #if TOOLS / Godot node types.
public class CollisionLayerContractTests
{
    [Test]
    public async Task godot_default_mask_maps_to_the_floor_index()
    {
        // Godot's default collision_layer is 1 → index 0 → 1<<0 = Floor.
        await Assert.That(CollisionLayerContract.MaskToLayerIndex(1u)).IsEqualTo(0);
        await Assert.That(CollisionLayerContract.IsMultiLayer(1u)).IsFalse();
    }

    [Test]
    public async Task obstacle_mask_maps_to_the_obstacle_index()
    {
        // The regression this PR fixes: obstacle mask 2 → index 1 → 1<<1 = Obstacle.
        await Assert.That(CollisionLayerContract.MaskToLayerIndex(2u)).IsEqualTo(1);
        await Assert.That(CollisionLayerContract.IsMultiLayer(2u)).IsFalse();
    }

    [Test]
    public async Task higher_single_bits_map_to_their_index()
    {
        await Assert.That(CollisionLayerContract.MaskToLayerIndex(1u << 5)).IsEqualTo(5);
        await Assert.That(CollisionLayerContract.MaskToLayerIndex(1u << 31)).IsEqualTo(31);
    }

    [Test]
    public async Task unlayered_mask_maps_to_index_zero()
    {
        await Assert.That(CollisionLayerContract.MaskToLayerIndex(0u)).IsEqualTo(0);
        await Assert.That(CollisionLayerContract.IsMultiLayer(0u)).IsFalse();
    }

    [Test]
    public async Task multi_layer_mask_collapses_to_the_lowest_bit_and_is_flagged()
    {
        // Mask 3 (bits 0 and 1) is lossy: it collapses to index 0, and IsMultiLayer flags it so
        // the exporter can warn rather than silently drop the higher bit.
        await Assert.That(CollisionLayerContract.MaskToLayerIndex(3u)).IsEqualTo(0);
        await Assert.That(CollisionLayerContract.IsMultiLayer(3u)).IsTrue();
        // Lowest bit is 1 (index 1) even though a higher bit is also set.
        await Assert.That(CollisionLayerContract.MaskToLayerIndex(6u)).IsEqualTo(1);
        await Assert.That(CollisionLayerContract.IsMultiLayer(6u)).IsTrue();
    }
}
