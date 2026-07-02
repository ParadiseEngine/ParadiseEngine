using System.Numerics;

namespace Paradise.Physics.Test;

public class GroundSupportTests
{
    private const float Dt = 1f / 60f;

    // A 20×20 slab whose top face is y = 0, covering x ∈ [0,20], z ∈ [0,20].
    private static CollisionWorld Slab()
    {
        Span<Collider> colliders = [Collider.CreateBox(new Vector3(10f, 0.5f, 10f))];
        Span<RigidTransform> transforms = [new RigidTransform(new Vector3(10f, -0.5f, 10f), Quaternion.Identity)];
        return CollisionWorld.Build(colliders, transforms);
    }

    private static PlanarDynamicsSettings Supported => PlanarDynamicsSettings.Default with
    {
        RequireSupport = true,
        SupportFilter = CollisionFilter.Default,
        SupportProbeDepth = 10f,
    };

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

    [Test]
    public async Task sphere_stops_at_the_slab_edge_instead_of_leaving()
    {
        using CollisionWorld slab = Slab();
        DynamicSphere[] spheres = [new()
        {
            Position = new Vector3(2f, 0.85f, 5f),
            Velocity = new Vector3(-8f, 0f, 0f), // straight at the x = 0 edge
            Radius = 0.35f,
            Mass = 1f,
        }];

        for (int i = 0; i < 300; i++)
        {
            PlanarSphereDynamics.Step(spheres, [], slab, Supported, Dt);
            await Assert.That(spheres[0].Position.X).IsGreaterThanOrEqualTo(-1e-3f);
        }

        await Assert.That(spheres[0].Velocity).IsEqualTo(Vector3.Zero); // rests at the rim
        await Assert.That(spheres[0].Position.Y).IsEqualTo(0.85f);
    }

    [Test]
    public async Task sphere_slides_along_the_edge_on_a_diagonal_approach()
    {
        using CollisionWorld slab = Slab();
        DynamicSphere[] spheres = [new()
        {
            Position = new Vector3(2f, 0.85f, 5f),
            Velocity = new Vector3(-8f, 0f, 3f),
            Radius = 0.35f,
            Mass = 1f,
        }];

        for (int i = 0; i < 300; i++)
        {
            PlanarSphereDynamics.Step(spheres, [], slab, Supported, Dt);
        }

        await Assert.That(spheres[0].Position.X).IsGreaterThanOrEqualTo(-1e-3f); // held by the edge
        await Assert.That(spheres[0].Position.Z).IsGreaterThan(5.5f);            // kept sliding in Z
    }
}
