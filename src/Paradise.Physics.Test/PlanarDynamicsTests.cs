using System.Numerics;

namespace Paradise.Physics.Test;

public class PlanarDynamicsTests
{
    private const float Dt = 1f / 60f;

    private static CollisionWorld WallAtX5()
    {
        // Box face at x = 5, tall and long so contacts are always horizontal.
        Span<Collider> colliders = [Collider.CreateBox(new Vector3(1f, 10f, 10f))];
        Span<RigidTransform> transforms = [new RigidTransform(new Vector3(6f, 0f, 0f), Quaternion.Identity)];
        return CollisionWorld.Build(colliders, transforms);
    }

    private static DynamicSphere Ball(Vector3 position, Vector3 velocity, float radius = 0.35f, float mass = 1f,
        float damping = 0f, float restitution = 0.6f)
        => new()
        {
            Position = position, Velocity = velocity, Radius = radius, Mass = mass,
            LinearDamping = damping, Restitution = restitution,
        };

    [Test]
    public async Task damping_brings_a_rolling_sphere_to_rest()
    {
        DynamicSphere[] spheres = [Ball(new Vector3(0f, 0.85f, 0f), new Vector3(2f, 0f, 0f), damping: 1.5f)];
        for (int i = 0; i < 600; i++)
        {
            PlanarSphereDynamics.Step(spheres, [], statics: null, PlanarDynamicsSettings.Default, Dt);
        }

        await Assert.That(spheres[0].Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(spheres[0].Position.X).IsGreaterThan(0.1f);   // it did roll
        await Assert.That(spheres[0].Position.X).IsLessThan(2f / 1.5f); // bounded by damping
    }

    [Test]
    public async Task sphere_bounces_off_wall_with_restitution()
    {
        CollisionWorld statics = WallAtX5();
        PlanarDynamicsSettings settings = PlanarDynamicsSettings.Default;
        DynamicSphere[] spheres = [Ball(new Vector3(0f, 0f, 0f), new Vector3(5f, 0f, 0f), radius: 0.5f)];

        float incomingSpeed = 5f;
        for (int i = 0; i < 120; i++)
        {
            PlanarSphereDynamics.Step(spheres, [], statics, settings, Dt);
            await Assert.That(spheres[0].Position.X).IsLessThanOrEqualTo(5f - 0.5f + 1e-3f); // never penetrates
        }

        await Assert.That(spheres[0].Velocity.X).IsLessThan(0f); // reflected
        float outgoingSpeed = MathF.Abs(spheres[0].Velocity.X);
        await Assert.That(MathF.Abs(outgoingSpeed - settings.StaticRestitution * incomingSpeed)).IsLessThan(0.05f);
    }

    [Test]
    public async Task equal_masses_swap_momentum_head_on_when_elastic()
    {
        PlanarDynamicsSettings settings = PlanarDynamicsSettings.Default;
        DynamicSphere[] spheres =
        [
            Ball(new Vector3(0f, 0.85f, 0f), new Vector3(3f, 0f, 0f), restitution: 1f),
            Ball(new Vector3(2f, 0.85f, 0f), Vector3.Zero, restitution: 1f),
        ];

        for (int i = 0; i < 180; i++)
        {
            PlanarSphereDynamics.Step(spheres, [], statics: null, settings, Dt);
        }

        // Elastic equal-mass head-on: A stops, B carries the full speed.
        await Assert.That(MathF.Abs(spheres[0].Velocity.X)).IsLessThan(0.05f);
        await Assert.That(MathF.Abs(spheres[1].Velocity.X - 3f)).IsLessThan(0.05f);
        // And they end separated.
        float distance = Vector3.Distance(spheres[0].Position, spheres[1].Position);
        await Assert.That(distance).IsGreaterThanOrEqualTo(0.7f - 1e-3f);
    }

    [Test]
    public async Task pair_collisions_report_contact_impulses()
    {
        PlanarDynamicsSettings settings = PlanarDynamicsSettings.Default;
        DynamicSphere[] spheres =
        [
            Ball(new Vector3(0f, 0.85f, 0f), new Vector3(3f, 0f, 0f), restitution: 1f),
            Ball(new Vector3(2f, 0.85f, 0f), Vector3.Zero, restitution: 1f),
        ];

        // Approach: no contact yet -> zero impulse on both.
        PlanarSphereDynamics.Step(spheres, [], statics: null, settings, Dt);
        await Assert.That(spheres[0].ContactImpulse).IsEqualTo(0f);
        await Assert.That(spheres[1].ContactImpulse).IsEqualTo(0f);

        float peak = 0f;
        for (int i = 0; i < 180; i++)
        {
            PlanarSphereDynamics.Step(spheres, [], statics: null, settings, Dt);
            peak = MathF.Max(peak, spheres[0].ContactImpulse);
            // Both partners of a pair report the same accumulated magnitude.
            await Assert.That(MathF.Abs(spheres[0].ContactImpulse - spheres[1].ContactImpulse)).IsLessThan(1e-4f);
        }

        // Elastic equal-mass hit at 3 m/s, unit masses: impulse = (1+e)*|dv|/(1/mA+1/mB) = 3.
        await Assert.That(MathF.Abs(peak - 3f)).IsLessThan(0.2f);
        // The step is an OUTPUT per call: once separated, it resets to zero.
        await Assert.That(spheres[0].ContactImpulse).IsEqualTo(0f);
    }

    [Test]
    public async Task kinematic_pusher_depenetrates_and_injects_velocity()
    {
        var pusher = new KinematicCapsule
        {
            Position = new Vector3(0f, 0.9f, 0f),
            Velocity = new Vector3(1f, 0f, 0f),
            Radius = 0.4f,
            HalfLength = 0.5f,
        };
        // Sphere overlapping the capsule wall on the +X side.
        DynamicSphere[] spheres = [Ball(new Vector3(0.5f, 0.85f, 0f), Vector3.Zero, radius: 0.3f)];

        PlanarSphereDynamics.Step(spheres, [pusher], statics: null, PlanarDynamicsSettings.Default, Dt);

        // Depenetrated: horizontal center distance ≥ radii sum + skin (within tolerance)…
        float horizontal = spheres[0].Position.X; // push is along +X
        await Assert.That(horizontal).IsGreaterThan(0.5f);
        // …and carried along: normal velocity ≈ pusherSpeed × PushStrength.
        await Assert.That(spheres[0].Velocity.X).IsGreaterThanOrEqualTo(1f * PlanarDynamicsSettings.Default.PushStrength - 0.05f);
        await Assert.That(spheres[0].Position.Y).IsEqualTo(0.85f);
    }

    [Test]
    public async Task second_pass_pushes_an_overlapping_sphere_out_of_a_wall()
    {
        CollisionWorld statics = WallAtX5();
        // Center outside the box but surface overlapping (gap 4.7 → penetration 0.2), zero velocity
        // so the integrate step does nothing — only the depenetration pass can fix it.
        DynamicSphere[] spheres = [Ball(new Vector3(4.7f, 0f, 0f), Vector3.Zero, radius: 0.5f)];

        PlanarSphereDynamics.Step(spheres, [], statics, PlanarDynamicsSettings.Default, Dt);

        await Assert.That(spheres[0].Position.X).IsLessThanOrEqualTo(5f - 0.5f - PlanarDynamicsSettings.Default.Skin + 1e-3f);
    }

    [Test]
    public async Task y_is_never_modified()
    {
        CollisionWorld statics = WallAtX5();
        var pusher = new KinematicCapsule
        {
            Position = new Vector3(3f, 0.9f, 0f),
            Velocity = new Vector3(2f, 0f, 0f),
            Radius = 0.4f,
            HalfLength = 0.5f,
        };
        DynamicSphere[] spheres =
        [
            Ball(new Vector3(3.6f, 0.85f, 0f), new Vector3(4f, 0f, 0f)),
            Ball(new Vector3(4.1f, 0.85f, 0.1f), Vector3.Zero),
        ];

        for (int i = 0; i < 240; i++)
        {
            PlanarSphereDynamics.Step(spheres, [pusher], statics, PlanarDynamicsSettings.Default, Dt);
            await Assert.That(spheres[0].Position.Y).IsEqualTo(0.85f); // bitwise
            await Assert.That(spheres[1].Position.Y).IsEqualTo(0.85f);
        }
    }

    [Test]
    public async Task per_sphere_damping_changes_travel_distance()
    {
        DynamicSphere[] spheres =
        [
            Ball(new Vector3(0f, 0.85f, 0f), new Vector3(2f, 0f, 0f), damping: 0.5f),
            Ball(new Vector3(0f, 0.85f, 5f), new Vector3(2f, 0f, 0f), damping: 3f),
        ];
        for (int i = 0; i < 1200; i++)
        {
            PlanarSphereDynamics.Step(spheres, [], statics: null, PlanarDynamicsSettings.Default, Dt);
        }

        await Assert.That(spheres[0].Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(spheres[1].Velocity).IsEqualTo(Vector3.Zero);
        // Same launch speed: the lightly damped ball rolls far past the heavily damped one
        // (analytic bounds: v0/damping = 4 m and 2/3 m respectively).
        await Assert.That(spheres[0].Position.X).IsGreaterThan(2f * spheres[1].Position.X);
    }

    [Test]
    public async Task pair_restitution_is_the_average_of_both_spheres()
    {
        // e = (1 + 0) / 2 = 0.5: elastic-vs-plastic head-on with equal masses.
        // Post-impact (1-D, mA = mB, B at rest): vA = v0(1−e)/2, vB = v0(1+e)/2.
        DynamicSphere[] spheres =
        [
            Ball(new Vector3(0f, 0.85f, 0f), new Vector3(3f, 0f, 0f), restitution: 1f),
            Ball(new Vector3(2f, 0.85f, 0f), Vector3.Zero, restitution: 0f),
        ];

        for (int i = 0; i < 120; i++)
        {
            PlanarSphereDynamics.Step(spheres, [], statics: null, PlanarDynamicsSettings.Default, Dt);
        }

        await Assert.That(MathF.Abs(spheres[0].Velocity.X - 0.75f)).IsLessThan(0.05f);
        await Assert.That(MathF.Abs(spheres[1].Velocity.X - 2.25f)).IsLessThan(0.05f);
    }

    [Test]
    public async Task steps_are_bitwise_deterministic()
    {
        CollisionWorld statics = WallAtX5();
        var results = new List<int>[2];

        for (int pass = 0; pass < 2; pass++)
        {
            DynamicSphere[] spheres =
            [
                Ball(new Vector3(0f, 0.85f, 0f), new Vector3(4f, 0f, 0.3f)),
                Ball(new Vector3(2f, 0.85f, 0.2f), Vector3.Zero),
                Ball(new Vector3(3.5f, 0.85f, -0.3f), new Vector3(-1f, 0f, 0f)),
            ];
            var pusher = new KinematicCapsule
            {
                Position = new Vector3(-1f, 0.9f, 0f),
                Velocity = new Vector3(3f, 0f, 0f),
                Radius = 0.4f,
                HalfLength = 0.5f,
            };
            for (int i = 0; i < 300; i++)
            {
                PlanarSphereDynamics.Step(spheres, [pusher], statics, PlanarDynamicsSettings.Default, Dt);
            }

            var sink = new List<int>();
            foreach (DynamicSphere s in spheres)
            {
                sink.Add(BitConverter.SingleToInt32Bits(s.Position.X));
                sink.Add(BitConverter.SingleToInt32Bits(s.Position.Z));
                sink.Add(BitConverter.SingleToInt32Bits(s.Velocity.X));
                sink.Add(BitConverter.SingleToInt32Bits(s.Velocity.Z));
            }
            results[pass] = sink;
        }

        await Assert.That(results[0].SequenceEqual(results[1])).IsTrue();
    }
}
