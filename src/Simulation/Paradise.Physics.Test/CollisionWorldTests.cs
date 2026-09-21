using System.Numerics;

namespace Paradise.Physics.Test;

public class CollisionWorldTests
{
    private static bool Approx(float a, float b, float tolerance = 1e-3f) => MathF.Abs(a - b) <= tolerance;

    private static CollisionWorld TwoBoxesAlongX(CollisionFilter? firstFilter = null)
    {
        Span<Collider> colliders = [
            Collider.CreateBox(new Vector3(1f, 1f, 1f), firstFilter ?? CollisionFilter.Default),
            Collider.CreateBox(new Vector3(1f, 1f, 1f)),
        ];
        Span<RigidTransform> transforms = [
            new RigidTransform(new Vector3(5f, 0f, 0f), Quaternion.Identity),
            new RigidTransform(new Vector3(10f, 0f, 0f), Quaternion.Identity),
        ];
        return CollisionWorld.Build(colliders, transforms);
    }

    [Test]
    public async Task cast_ray_returns_the_closest_of_many()
    {
        CollisionWorld world = TwoBoxesAlongX();
        var input = new RaycastInput { Start = Vector3.Zero, End = new Vector3(20f, 0f, 0f), Filter = CollisionFilter.Default };

        bool hit = world.CastRay(input, out RaycastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(result.BodyIndex).IsEqualTo(0);
        await Assert.That(Approx(result.Fraction, 4f / 20f)).IsTrue();
    }

    [Test]
    public async Task cast_ray_filter_skips_excluded_bodies()
    {
        var obstacleOnly = new CollisionFilter { BelongsTo = 2u, CollidesWith = ~0u };
        CollisionWorld world = TwoBoxesAlongX(firstFilter: obstacleOnly);
        var input = new RaycastInput
        {
            Start = Vector3.Zero,
            End = new Vector3(20f, 0f, 0f),
            Filter = new CollisionFilter { BelongsTo = ~0u, CollidesWith = 1u }, // ignores layer 2
        };

        bool hit = world.CastRay(input, out RaycastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(result.BodyIndex).IsEqualTo(1);
        await Assert.That(Approx(result.Fraction, 9f / 20f)).IsTrue();
    }

    [Test]
    public async Task equal_fraction_tie_breaks_to_the_lowest_body_index()
    {
        Span<Collider> colliders = [Collider.CreateSphere(1f), Collider.CreateSphere(1f)];
        Span<RigidTransform> transforms = [RigidTransform.Identity, RigidTransform.Identity];
        CollisionWorld world = CollisionWorld.Build(colliders, transforms);

        bool hit = world.CastRay(new RaycastInput { Start = new(-5f, 0f, 0f), End = new(5f, 0f, 0f), Filter = CollisionFilter.Default }, out RaycastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(result.BodyIndex).IsEqualTo(0);
    }

    [Test]
    public async Task world_query_matches_direct_narrowphase_for_rotated_long_box()
    {
        // A long thin box rotated 45° has a much larger AABB than its geometry —
        // the prefilter must never cull a true hit nor invent one.
        var box = Collider.CreateBox(new Vector3(5f, 0.1f, 0.1f));
        var transform = new RigidTransform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f));
        CollisionWorld world = CollisionWorld.Build([box], [transform]);

        var input = new RaycastInput { Start = new(2f, 0f, -5f), End = new(2f, 0f, 5f), Filter = CollisionFilter.Default };
        bool worldHit = world.CastRay(input, out RaycastHit worldResult);
        bool directHit = ColliderQueries.RayCollider(input, box, transform, out RaycastHit directResult);

        await Assert.That(worldHit).IsEqualTo(directHit);
        if (worldHit)
        {
            await Assert.That(worldResult.Fraction).IsEqualTo(directResult.Fraction);
        }
    }

    [Test]
    public async Task cast_collider_picks_the_nearer_obstacle()
    {
        CollisionWorld world = TwoBoxesAlongX();
        var input = new ColliderCastInput
        {
            Collider = Collider.CreateCapsule(0.4f, 0.5f),
            Orientation = Quaternion.Identity,
            Start = Vector3.Zero,
            End = new Vector3(20f, 0f, 0f),
        };

        bool hit = world.CastCollider(input, out ColliderCastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(result.BodyIndex).IsEqualTo(0);
        // Contact when the capsule wall (r=0.4) reaches the box face at x=4 → center at 3.6.
        await Assert.That(Approx(result.Fraction, 3.6f / 20f, 5e-3f)).IsTrue();
    }

    [Test]
    public async Task calculate_distance_respects_max_distance()
    {
        CollisionWorld world = TwoBoxesAlongX();
        var nearInput = new ColliderDistanceInput
        {
            Collider = Collider.CreateSphere(0.5f),
            Transform = new RigidTransform(new Vector3(2f, 0f, 0f), Quaternion.Identity),
            MaxDistance = 10f,
        };
        var strictInput = nearInput with { MaxDistance = 1f };

        bool foundNear = world.CalculateDistance(nearInput, out DistanceHit near);
        bool foundStrict = world.CalculateDistance(strictInput, out _);

        await Assert.That(foundNear).IsTrue();
        await Assert.That(near.BodyIndex).IsEqualTo(0);
        await Assert.That(Approx(near.Distance, 1.5f)).IsTrue(); // gap 2 − 0.5 sphere radius
        await Assert.That(foundStrict).IsFalse();
    }

    [Test]
    public async Task build_rejects_mismatched_span_lengths()
    {
        await Assert.That(() =>
        {
            Span<Collider> colliders = [Collider.CreateSphere(1f)];
            Span<RigidTransform> transforms = [];
            CollisionWorld.Build(colliders, transforms);
        }).Throws<ArgumentException>();
    }

    [Test]
    public async Task repeated_queries_are_bitwise_identical()
    {
        CollisionWorld world = TwoBoxesAlongX();
        var first = new List<int>();
        var second = new List<int>();

        for (int pass = 0; pass < 2; pass++)
        {
            var sink = pass == 0 ? first : second;
            var passRandom = new Random(777);
            for (int i = 0; i < 200; i++)
            {
                var start = new Vector3(
                    (float)(passRandom.NextDouble() * 30.0 - 5.0),
                    (float)(passRandom.NextDouble() * 6.0 - 3.0),
                    (float)(passRandom.NextDouble() * 6.0 - 3.0));
                var end = new Vector3(
                    (float)(passRandom.NextDouble() * 30.0 - 5.0),
                    (float)(passRandom.NextDouble() * 6.0 - 3.0),
                    (float)(passRandom.NextDouble() * 6.0 - 3.0));

                if (world.CastRay(new RaycastInput { Start = start, End = end, Filter = CollisionFilter.Default }, out RaycastHit ray))
                {
                    sink.Add(BitConverter.SingleToInt32Bits(ray.Fraction));
                    sink.Add(BitConverter.SingleToInt32Bits(ray.SurfaceNormal.X));
                }

                var castInput = new ColliderCastInput { Collider = Collider.CreateSphere(0.4f), Orientation = Quaternion.Identity, Start = start, End = end };
                if (world.CastCollider(castInput, out ColliderCastHit cast))
                {
                    sink.Add(BitConverter.SingleToInt32Bits(cast.Fraction));
                    sink.Add(BitConverter.SingleToInt32Bits(cast.SurfaceNormal.X));
                }
            }
        }

        await Assert.That(first.SequenceEqual(second)).IsTrue();
        await Assert.That(first.Count > 0).IsTrue();
    }
}
