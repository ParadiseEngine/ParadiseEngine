using System.Numerics;

namespace Paradise.Physics.Test;

public class SphereDynamicsTests
{
    private const float Dt = 1f / 60f;

    // Big flat floor with its top surface at y = 0 (balls rest at y = radius).
    private static CollisionWorld Floor()
    {
        Span<Collider> colliders = [Collider.CreateBox(new Vector3(50f, 1f, 50f))];
        Span<RigidTransform> transforms = [new RigidTransform(new Vector3(0f, -1f, 0f), Quaternion.Identity)];
        return CollisionWorld.Build(colliders, transforms);
    }

    // Floor plus a vertical wall whose −X face is at x = 5.
    private static CollisionWorld FloorAndWallAtX5()
    {
        Span<Collider> colliders =
        [
            Collider.CreateBox(new Vector3(50f, 1f, 50f)),
            Collider.CreateBox(new Vector3(1f, 10f, 10f)),
        ];
        Span<RigidTransform> transforms =
        [
            new RigidTransform(new Vector3(0f, -1f, 0f), Quaternion.Identity),
            new RigidTransform(new Vector3(6f, 0f, 0f), Quaternion.Identity),
        ];
        return CollisionWorld.Build(colliders, transforms);
    }

    private static DynamicSphere Ball(Vector3 position, Vector3 velocity, float radius = 0.5f, float mass = 1f,
        float restitution = 0.4f, float friction = 0.3f, Vector3 angularVelocity = default,
        float linearDamping = 0f, float angularDamping = 0f)
        => new()
        {
            Position = position, Velocity = velocity, AngularVelocity = angularVelocity,
            Radius = radius, Mass = mass, Restitution = restitution, Friction = friction,
            LinearDamping = linearDamping, AngularDamping = angularDamping,
        };

    [Test]
    public async Task a_dropped_ball_settles_on_the_floor()
    {
        CollisionWorld statics = Floor();
        DynamicSphere[] s = [Ball(new Vector3(0f, 2f, 0f), Vector3.Zero, radius: 0.5f)];
        for (int i = 0; i < 400; i++)
        {
            RigidSphereDynamics.Step(s, [], statics, SphereDynamicsSettings.Default, Dt);
        }

        await Assert.That(s[0].Position.Y).IsGreaterThan(0.45f); // rests ON the felt, doesn't sink
        await Assert.That(s[0].Position.Y).IsLessThan(0.55f);    // and doesn't hover
        await Assert.That(s[0].Velocity.Length()).IsLessThan(0.1f); // settled, not bouncing forever
    }

    [Test]
    public async Task gravity_pulls_a_free_ball_down()
    {
        DynamicSphere[] s = [Ball(new Vector3(0f, 0f, 0f), Vector3.Zero)];
        for (int i = 0; i < 30; i++)
        {
            RigidSphereDynamics.Step(s, [], statics: null, SphereDynamicsSettings.Default, Dt);
        }
        await Assert.That(s[0].Position.Y).IsLessThan(-0.5f);   // fell
        await Assert.That(s[0].Velocity.Y).IsLessThan(-3f);     // accelerating downward
    }

    [Test]
    public async Task a_sliding_ball_develops_rolling_spin()
    {
        // A ball sliding +X with no spin picks up roll from cloth friction: natural roll about −Z
        // (ω = Up × v / r). Friction couples linear → angular.
        CollisionWorld statics = Floor();
        var settings = SphereDynamicsSettings.Default with { StaticFriction = 0.4f };
        DynamicSphere[] s = [Ball(new Vector3(0f, 0.5f, 0f), new Vector3(3f, 0f, 0f), friction: 0.4f)];
        for (int i = 0; i < 60; i++)
        {
            RigidSphereDynamics.Step(s, [], statics, settings, Dt);
        }
        await Assert.That(s[0].AngularVelocity.Z).IsLessThan(-1f); // developed forward roll
        await Assert.That(s[0].Position.X).IsGreaterThan(0.5f);    // still travelled forward
    }

    [Test]
    public async Task a_spinning_ball_at_rest_is_driven_by_friction()
    {
        // Pure spin, no linear velocity: cloth friction converts it into linear motion (the ball
        // "walks"), and the spin bleeds down.
        CollisionWorld statics = Floor();
        var settings = SphereDynamicsSettings.Default with { StaticFriction = 0.6f };
        DynamicSphere[] s = [Ball(new Vector3(0f, 0.5f, 0f), Vector3.Zero, friction: 0.6f,
            angularVelocity: new Vector3(0f, 0f, 20f))];
        for (int i = 0; i < 60; i++)
        {
            RigidSphereDynamics.Step(s, [], statics, settings, Dt);
        }
        await Assert.That(MathF.Abs(s[0].Position.X)).IsGreaterThan(0.05f);   // walked off its spin
        await Assert.That(MathF.Abs(s[0].AngularVelocity.Z)).IsLessThan(20f); // spin bled
    }

    [Test]
    public async Task sidespin_deflects_a_cushion_rebound()
    {
        // English (ω.y) at the wall contact bends the rebound tangentially vs a spinless control.
        CollisionWorld statics = FloorAndWallAtX5();
        var settings = SphereDynamicsSettings.Default with { StaticFriction = 0.4f };
        DynamicSphere[] spun = [Ball(new Vector3(0f, 0.5f, 0f), new Vector3(6f, 0f, 0f), friction: 0.4f,
            angularVelocity: new Vector3(0f, 30f, 0f))];
        DynamicSphere[] plain = [Ball(new Vector3(0f, 0.5f, 0f), new Vector3(6f, 0f, 0f), friction: 0.4f)];
        for (int i = 0; i < 90; i++)
        {
            RigidSphereDynamics.Step(spun, [], statics, settings, Dt);
            RigidSphereDynamics.Step(plain, [], statics, settings, Dt);
        }
        await Assert.That(spun[0].Velocity.X).IsLessThan(0f);           // rebounded off the wall
        await Assert.That(MathF.Abs(spun[0].Position.Z - plain[0].Position.Z)).IsGreaterThan(0.1f); // english bent it
    }

    [Test]
    public async Task a_ball_can_jump_and_land_back_on_the_felt()
    {
        CollisionWorld statics = Floor();
        DynamicSphere[] s = [Ball(new Vector3(0f, 0.5f, 0f), new Vector3(2f, 4f, 0f))];
        float maxY = 0f;
        for (int i = 0; i < 120; i++)
        {
            RigidSphereDynamics.Step(s, [], statics, SphereDynamicsSettings.Default, Dt);
            maxY = MathF.Max(maxY, s[0].Position.Y);
        }
        await Assert.That(maxY).IsGreaterThan(0.9f);          // it left the felt (jumped)
        await Assert.That(s[0].Position.Y).IsLessThan(0.6f);  // and landed back near rest
    }

    [Test]
    public async Task equal_masses_swap_momentum_head_on()
    {
        // Elastic, frictionless, on the floor: A stops, B carries the speed.
        CollisionWorld statics = Floor();
        var settings = SphereDynamicsSettings.Default with { StaticFriction = 0f };
        DynamicSphere[] s =
        [
            Ball(new Vector3(0f, 0.5f, 0f), new Vector3(3f, 0f, 0f), restitution: 1f, friction: 0f),
            Ball(new Vector3(1.2f, 0.5f, 0f), Vector3.Zero, restitution: 1f, friction: 0f),
        ];
        for (int i = 0; i < 60; i++)
        {
            RigidSphereDynamics.Step(s, [], statics, settings, Dt);
        }
        await Assert.That(s[1].Velocity.X).IsGreaterThan(2f);            // B carries the momentum
        await Assert.That(s[0].Velocity.X).IsLessThan(s[1].Velocity.X); // A trails B
        float distance = Vector3.Distance(s[0].Position, s[1].Position);
        await Assert.That(distance).IsGreaterThanOrEqualTo(1f - 1e-2f);  // separated
    }

    [Test]
    public async Task pair_collisions_report_contact_impulses()
    {
        CollisionWorld statics = Floor();
        var settings = SphereDynamicsSettings.Default with { StaticFriction = 0f };
        DynamicSphere[] s =
        [
            Ball(new Vector3(0f, 0.5f, 0f), new Vector3(3f, 0f, 0f), restitution: 1f, friction: 0f),
            Ball(new Vector3(1.2f, 0.5f, 0f), Vector3.Zero, restitution: 1f, friction: 0f),
        ];
        float peak = 0f;
        for (int i = 0; i < 60; i++)
        {
            RigidSphereDynamics.Step(s, [], statics, settings, Dt);
            peak = MathF.Max(peak, s[0].ContactImpulse);
            await Assert.That(MathF.Abs(s[0].ContactImpulse - s[1].ContactImpulse)).IsLessThan(1e-3f);
        }
        await Assert.That(peak).IsGreaterThan(1f); // the hit registered
    }

    [Test]
    public async Task steps_are_bitwise_deterministic()
    {
        var results = new List<int>[2];
        for (int pass = 0; pass < 2; pass++)
        {
            CollisionWorld statics = FloorAndWallAtX5();
            DynamicSphere[] s =
            [
                Ball(new Vector3(0f, 0.5f, 0f), new Vector3(4f, 0f, 0.3f), angularVelocity: new Vector3(1f, 5f, 0f)),
                Ball(new Vector3(2f, 0.5f, 0.2f), Vector3.Zero),
                Ball(new Vector3(3.5f, 0.5f, -0.3f), new Vector3(-1f, 1f, 0f)),
            ];
            for (int i = 0; i < 300; i++)
            {
                RigidSphereDynamics.Step(s, [], statics, SphereDynamicsSettings.Default, Dt);
            }
            var sink = new List<int>();
            foreach (DynamicSphere b in s)
            {
                sink.Add(BitConverter.SingleToInt32Bits(b.Position.X));
                sink.Add(BitConverter.SingleToInt32Bits(b.Position.Y));
                sink.Add(BitConverter.SingleToInt32Bits(b.Position.Z));
                sink.Add(BitConverter.SingleToInt32Bits(b.Velocity.X));
                sink.Add(BitConverter.SingleToInt32Bits(b.AngularVelocity.Y));
            }
            results[pass] = sink;
        }
        await Assert.That(results[0].SequenceEqual(results[1])).IsTrue();
    }

    [Test]
    public async Task cushion_bounce_uses_static_restitution_not_the_pairwise_value()
    {
        // The wall (static) bounce is driven by settings.StaticRestitution, NOT the ball's own
        // Restitution (which is the ball↔ball coefficient). An elastic ball (1.0) off a dead wall
        // (0.2) rebounds SLOW — decoupling the two so they can't silently re-converge.
        CollisionWorld statics = FloorAndWallAtX5();
        var settings = SphereDynamicsSettings.Default with { StaticRestitution = 0.2f, StaticFriction = 0f };
        DynamicSphere[] s = [Ball(new Vector3(0f, 0.5f, 0f), new Vector3(5f, 0f, 0f),
            radius: 0.5f, restitution: 1f, friction: 0f)];
        for (int i = 0; i < 120; i++)
        {
            RigidSphereDynamics.Step(s, [], statics, settings, Dt);
        }
        await Assert.That(s[0].Velocity.X).IsLessThan(0f);            // rebounded off the wall
        await Assert.That(MathF.Abs(s[0].Velocity.X)).IsLessThan(2f); // ≈0.2×5, NOT elastic (~5)
    }

    [Test]
    public async Task kinematic_pusher_shoves_a_ball_horizontally()
    {
        CollisionWorld statics = Floor();
        var pusher = new KinematicCapsule
        {
            Position = new Vector3(0f, 0.5f, 0f), Velocity = new Vector3(1f, 0f, 0f),
            Radius = 0.4f, HalfLength = 0.5f,
        };
        DynamicSphere[] s = [Ball(new Vector3(0.7f, 0.5f, 0f), Vector3.Zero, radius: 0.3f)];
        for (int i = 0; i < 30; i++)
        {
            RigidSphereDynamics.Step(s, [pusher], statics, SphereDynamicsSettings.Default, Dt);
        }
        await Assert.That(s[0].Position.X).IsGreaterThan(0.7f);        // pushed along +X
        await Assert.That(MathF.Abs(s[0].Position.Y - 0.3f)).IsLessThan(0.1f); // stayed on the felt
    }
}
