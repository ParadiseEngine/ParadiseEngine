using System.Numerics;

namespace Paradise.Physics.Test;

public class GroundSupportTests
{
    // A 20×20 slab whose top face is y = 0, covering x ∈ [0,20], z ∈ [0,20].
    private static CollisionWorld Slab()
    {
        Span<Collider> colliders = [Collider.CreateBox(new Vector3(10f, 0.5f, 10f))];
        Span<RigidTransform> transforms = [new RigidTransform(new Vector3(10f, -0.5f, 10f), Quaternion.Identity)];
        return CollisionWorld.Build(colliders, transforms);
    }

    // NOTE: PlanarGroundSupport.Clamp is still used for CHARACTER (capsule) ground containment.
    // The old sphere-dynamics "RequireSupport" ground clamp is gone — under gravity a sphere that
    // rolls off an edge simply falls (real 3D), so those sphere-edge tests were removed.

    [Test]
    public async Task clamp_accepts_supported_moves_verbatim()
    {
        using CollisionWorld slab = Slab();
        var from = new Vector3(5f, 0.85f, 5f);
        var to = new Vector3(6f, 0.85f, 7f);
        Vector3 result = PlanarGroundSupport.Clamp(slab, CollisionFilter.Default, from, to, 10f);
        await Assert.That(result).IsEqualTo(to);
    }

    [Test]
    public async Task clamp_slides_along_the_edge_axis_by_axis()
    {
        using CollisionWorld slab = Slab();
        var from = new Vector3(0.5f, 0.85f, 5f);
        var to = new Vector3(-1f, 0.85f, 7f); // X leaves the slab, Z stays on it
        Vector3 result = PlanarGroundSupport.Clamp(slab, CollisionFilter.Default, from, to, 10f);
        await Assert.That(result.X).IsEqualTo(0.5f); // X move rejected
        await Assert.That(result.Z).IsEqualTo(7f);   // Z move kept
    }

    [Test]
    public async Task clamp_stays_put_when_both_axes_leave_the_slab()
    {
        using CollisionWorld slab = Slab();
        var from = new Vector3(0.2f, 0.85f, 0.2f);
        var to = new Vector3(-2f, 0.85f, -2f);
        Vector3 result = PlanarGroundSupport.Clamp(slab, CollisionFilter.Default, from, to, 10f);
        await Assert.That(result).IsEqualTo(from);
    }
}
