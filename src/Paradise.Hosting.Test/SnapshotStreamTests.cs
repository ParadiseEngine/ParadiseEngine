namespace Paradise.Hosting.Test;

public class SnapshotStreamTests
{
    [Test]
    public async Task snapshots_are_borrowed_in_publication_order_with_their_completed_frames()
    {
        var stream = new SnapshotStream<object>();
        var first = new object();
        var second = new object();
        stream.Publish(first, 17);
        stream.Publish(second, 18);

        await Assert.That(stream.TryRead(out var a)).IsTrue();
        await Assert.That(ReferenceEquals(a.World, first)).IsTrue();
        await Assert.That(a.Frame).IsEqualTo(17);
        await Assert.That(stream.TryRead(out var b)).IsTrue();
        await Assert.That(ReferenceEquals(b.World, second)).IsTrue();
        await Assert.That(b.Frame).IsEqualTo(18);
        await Assert.That(stream.TryRead(out _)).IsFalse();
        await Assert.That(stream.TryTakeRecycled(out _)).IsFalse();
    }

    [Test]
    public async Task returning_the_latest_snapshot_keeps_the_next_simulation_input_reserved()
    {
        var stream = new SnapshotStream<object>();
        var first = new object();
        stream.Publish(first, 0);
        stream.TryRead(out var snapshot);
        stream.Recycle(snapshot.World);

        await Assert.That(stream.TryTakeRecycled(out _)).IsFalse();

        stream.Publish(new object(), 1);

        await Assert.That(stream.TryTakeRecycled(out var recycled)).IsTrue();
        await Assert.That(ReferenceEquals(recycled, first)).IsTrue();
        await Assert.That(stream.TryTakeRecycled(out _)).IsFalse();
    }

    [Test]
    public async Task a_borrowed_world_survives_overflow_until_the_consumer_returns_it()
    {
        var stream = new SnapshotStream<object>(1);
        var borrowed = new object();
        var expired = new object();
        var newest = new object();
        stream.Publish(borrowed, 0);
        stream.TryRead(out _);
        stream.Publish(expired, 1);
        stream.Publish(newest, 2);

        await Assert.That(stream.TryTakeRecycled(out var recycled)).IsTrue();
        await Assert.That(ReferenceEquals(recycled, expired)).IsTrue();
        await Assert.That(stream.TryTakeRecycled(out _)).IsFalse();
        await Assert.That(stream.TryRead(out var snapshot)).IsTrue();
        await Assert.That(ReferenceEquals(snapshot.World, newest)).IsTrue();
        await Assert.That(stream.TryRead(out _)).IsFalse();

        stream.Recycle(borrowed);

        await Assert.That(stream.TryTakeRecycled(out recycled)).IsTrue();
        await Assert.That(ReferenceEquals(recycled, borrowed)).IsTrue();
    }

    [Test]
    public async Task overflow_keeps_only_the_newest_unread_snapshots()
    {
        var stream = new SnapshotStream<object>(2);
        var worlds = Enumerable.Range(0, 5).Select(_ => new object()).ToArray();
        for (var frame = 0; frame < worlds.Length; frame++)
        {
            stream.Publish(worlds[frame], frame);
        }

        for (var frame = 0; frame < 3; frame++)
        {
            await Assert.That(stream.TryTakeRecycled(out var world)).IsTrue();
            await Assert.That(ReferenceEquals(world, worlds[frame])).IsTrue();
        }
        await Assert.That(stream.TryTakeRecycled(out _)).IsFalse();
        await Assert.That(stream.TryRead(out var first)).IsTrue();
        await Assert.That(first.Frame).IsEqualTo(3);
        await Assert.That(stream.TryRead(out var second)).IsTrue();
        await Assert.That(second.Frame).IsEqualTo(4);
        await Assert.That(stream.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task recycling_requires_one_outstanding_consumer_borrow()
    {
        var stream = new SnapshotStream<object>();
        var world = new object();
        await Assert.That(() => stream.Recycle(world)).Throws<InvalidOperationException>();

        stream.Publish(world, 0);
        await Assert.That(() => stream.Recycle(world)).Throws<InvalidOperationException>();

        stream.TryRead(out _);
        stream.Recycle(world);
        await Assert.That(() => stream.Recycle(world)).Throws<InvalidOperationException>();

        stream.Publish(new object(), 1);
        await Assert.That(() => stream.Recycle(world)).Throws<InvalidOperationException>();

        stream.TryTakeRecycled(out _);
        await Assert.That(() => stream.Recycle(world)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task republishing_requires_reacquiring_the_world_from_the_pool()
    {
        var stream = new SnapshotStream<object>();
        var world = new object();
        stream.Publish(world, 0);
        await Assert.That(() => stream.Publish(world, 1)).Throws<InvalidOperationException>();

        stream.TryRead(out _);
        await Assert.That(() => stream.Publish(world, 1)).Throws<InvalidOperationException>();

        stream.Recycle(world);
        await Assert.That(() => stream.Publish(world, 1)).Throws<InvalidOperationException>();

        stream.Publish(new object(), 1);
        await Assert.That(() => stream.Publish(world, 2)).Throws<InvalidOperationException>();

        stream.TryTakeRecycled(out _);
        stream.Publish(world, 2);
        stream.TryRead(out _);
        await Assert.That(stream.TryRead(out var snapshot)).IsTrue();
        await Assert.That(ReferenceEquals(snapshot.World, world)).IsTrue();
        await Assert.That(snapshot.Frame).IsEqualTo(2);
    }

    [Test]
    public async Task distinct_worlds_with_value_equality_have_independent_ownership()
    {
        var stream = new SnapshotStream<EqualWorld>();
        var first = new EqualWorld(7);
        var second = new EqualWorld(7);
        stream.Publish(first, 0);
        stream.Publish(second, 1);
        stream.TryRead(out var snapshot);
        stream.Recycle(snapshot.World);

        await Assert.That(stream.TryTakeRecycled(out var recycled)).IsTrue();
        await Assert.That(ReferenceEquals(recycled, first)).IsTrue();
        await Assert.That(stream.TryRead(out snapshot)).IsTrue();
        await Assert.That(ReferenceEquals(snapshot.World, second)).IsTrue();
    }

    [Test]
    public async Task a_warmed_pool_does_not_allocate_per_publication()
    {
        var stream = new SnapshotStream<object>(2);
        var next = new object();
        stream.Publish(new object(), 0);

        static object PublishAndRecycle(SnapshotStream<object> snapshots, object world)
        {
            snapshots.TryRead(out var read);
            snapshots.Recycle(read.World);
            snapshots.Publish(world, 1);
            return snapshots.TryTakeRecycled(out var recycled) ? recycled : throw new InvalidOperationException();
        }

        for (var i = 0; i < 100; i++)
        {
            next = PublishAndRecycle(stream, next);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            next = PublishAndRecycle(stream, next);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task the_unread_capacity_must_be_positive()
    {
        await Assert.That(() => new SnapshotStream<object>(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SnapshotStream<object>(-1)).Throws<ArgumentOutOfRangeException>();
    }

    private sealed record EqualWorld(int Value);
}
