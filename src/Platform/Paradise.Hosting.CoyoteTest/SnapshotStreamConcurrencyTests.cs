using Microsoft.Coyote.Specifications;

namespace Paradise.Hosting.CoyoteTest;

public static class SnapshotStreamConcurrencyTests
{
    public static async Task LatestReturnRacingPoolRead_KeepsSimulationInputReserved()
    {
        var stream = new SnapshotStream<object>();
        var seed = new object();
        stream.Publish(seed, 0);

        var consumer = Task.Run(() =>
        {
            Specification.Assert(stream.TryRead(out var snapshot), "The seed snapshot must be available.");
            stream.Recycle(snapshot.World);
        });
        var producer = Task.Run(() =>
        {
            Specification.Assert(!stream.TryTakeRecycled(out _),
                "The latest snapshot returned to the producer while it is still the next simulation input.");
        });
        await Task.WhenAll(consumer, producer).ConfigureAwait(false);

        Specification.Assert(!stream.TryTakeRecycled(out _),
            "An early consumer return released the latest simulation input.");
        stream.Publish(new object(), 1);
        Specification.Assert(stream.TryTakeRecycled(out var recycled) && ReferenceEquals(recycled, seed),
            "Publishing a successor must release the returned simulation input.");
        Specification.Assert(!stream.TryTakeRecycled(out _), "A world was returned to the pool twice.");
    }

    public static async Task PublishRacingReturn_ReleasesThePreviousWorldExactlyOnce()
    {
        var stream = new SnapshotStream<object>();
        var seed = new object();
        stream.Publish(seed, 0);
        stream.TryRead(out var borrowed);

        var publish = Task.Run(() => stream.Publish(new object(), 1));
        var recycle = Task.Run(() => stream.Recycle(borrowed.World));
        await Task.WhenAll(publish, recycle).ConfigureAwait(false);

        Specification.Assert(stream.TryTakeRecycled(out var recycled) && ReferenceEquals(recycled, seed),
            "A return racing publication lost the previous world.");
        Specification.Assert(!stream.TryTakeRecycled(out _), "A return racing publication duplicated a world.");
    }

    public static async Task OverflowRacingBorrow_NeverRecyclesAWorldBeingRead()
    {
        var stream = new SnapshotStream<MutableWorld>(1);
        stream.Publish(new MutableWorld { Frame = 0 }, 0);

        var producer = Task.Run(async () =>
        {
            for (var frame = 1; frame <= 4; frame++)
            {
                var world = stream.TryTakeRecycled(out var recycled) ? recycled : new MutableWorld();
                world.Frame = frame;
                stream.Publish(world, frame);
                await Task.Yield();
            }
        });
        var consumer = Task.Run(async () =>
        {
            for (var sample = 0; sample < 4; sample++)
            {
                if (stream.TryRead(out var snapshot))
                {
                    await Task.Yield();
                    Specification.Assert(snapshot.World.Frame == snapshot.Frame,
                        "The producer overwrote a world while the consumer still held its snapshot.");
                    stream.Recycle(snapshot.World);
                }
                await Task.Yield();
            }
        });
        await Task.WhenAll(producer, consumer).ConfigureAwait(false);

        if (stream.TryRead(out var newest))
        {
            Specification.Assert(newest.Frame == 4 && newest.World.Frame == 4,
                "Overflow lost or overwrote the newest queued snapshot.");
            stream.Recycle(newest.World);
        }
        Specification.Assert(!stream.TryRead(out _), "The queue exceeded its unread capacity.");
        while (stream.TryTakeRecycled(out var world))
        {
            Specification.Assert(world.Frame != 4, "The pool exposed the newest simulation input.");
        }
    }

    public static async Task OverflowRacingReturn_PreservesEachWorldExactlyOnce()
    {
        var stream = new SnapshotStream<object>(1);
        var borrowed = new object();
        var expired = new object();
        var newest = new object();
        stream.Publish(borrowed, 0);
        stream.TryRead(out _);

        var producer = Task.Run(() =>
        {
            stream.Publish(expired, 1);
            stream.Publish(newest, 2);
        });
        var consumer = Task.Run(() => stream.Recycle(borrowed));
        await Task.WhenAll(producer, consumer).ConfigureAwait(false);

        var recycled = new HashSet<object>(ReferenceEqualityComparer.Instance);
        while (stream.TryTakeRecycled(out var world))
        {
            Specification.Assert(recycled.Add(world), "A world appeared in the recycle pool twice.");
        }
        Specification.Assert(recycled.Count == 2 && recycled.Contains(borrowed) && recycled.Contains(expired),
            "Queue trimming or concurrent return lost a world.");
        Specification.Assert(stream.TryRead(out var snapshot) && ReferenceEquals(snapshot.World, newest),
            "Queue trimming lost the newest publication.");
        Specification.Assert(!stream.TryRead(out _), "The queue exceeded its unread capacity.");
    }

    private sealed class MutableWorld
    {
        public long Frame { get; set; }
    }
}
