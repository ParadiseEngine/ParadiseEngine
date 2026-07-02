namespace Paradise.Physics.Test;

public class CollisionFilterTests
{
    [Test]
    public async Task default_filter_collides_with_everything()
    {
        await Assert.That(CollisionFilter.IsCollisionEnabled(CollisionFilter.Default, CollisionFilter.Default)).IsTrue();
    }

    [Test]
    public async Task collision_requires_mutual_agreement()
    {
        var floor = new CollisionFilter { BelongsTo = 1u, CollidesWith = ~0u };
        var character = new CollisionFilter { BelongsTo = 4u, CollidesWith = 2u }; // does not collide with layer 1

        await Assert.That(CollisionFilter.IsCollisionEnabled(character, floor)).IsFalse();
        await Assert.That(CollisionFilter.IsCollisionEnabled(floor, character)).IsFalse();

        var obstacle = new CollisionFilter { BelongsTo = 2u, CollidesWith = ~0u };
        await Assert.That(CollisionFilter.IsCollisionEnabled(character, obstacle)).IsTrue();
        await Assert.That(CollisionFilter.IsCollisionEnabled(obstacle, character)).IsTrue();
    }

    [Test]
    public async Task one_sided_masks_do_not_collide()
    {
        var a = new CollisionFilter { BelongsTo = 1u, CollidesWith = 2u };
        var b = new CollisionFilter { BelongsTo = 2u, CollidesWith = 4u }; // b does not collide with a's layer
        await Assert.That(CollisionFilter.IsCollisionEnabled(a, b)).IsFalse();
    }

    [Test]
    public async Task positive_group_index_forces_collision()
    {
        var a = new CollisionFilter { BelongsTo = 1u, CollidesWith = 0u, GroupIndex = 7 };
        var b = new CollisionFilter { BelongsTo = 2u, CollidesWith = 0u, GroupIndex = 7 };
        await Assert.That(CollisionFilter.IsCollisionEnabled(a, b)).IsTrue();
    }

    [Test]
    public async Task negative_group_index_suppresses_collision()
    {
        var a = new CollisionFilter { BelongsTo = ~0u, CollidesWith = ~0u, GroupIndex = -3 };
        var b = new CollisionFilter { BelongsTo = ~0u, CollidesWith = ~0u, GroupIndex = -3 };
        await Assert.That(CollisionFilter.IsCollisionEnabled(a, b)).IsFalse();
    }

    [Test]
    public async Task different_group_indices_fall_back_to_masks()
    {
        var a = new CollisionFilter { BelongsTo = ~0u, CollidesWith = ~0u, GroupIndex = 1 };
        var b = new CollisionFilter { BelongsTo = ~0u, CollidesWith = ~0u, GroupIndex = 2 };
        await Assert.That(CollisionFilter.IsCollisionEnabled(a, b)).IsTrue();
    }
}
