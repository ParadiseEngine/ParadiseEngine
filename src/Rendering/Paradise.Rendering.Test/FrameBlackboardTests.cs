using System.Runtime.CompilerServices;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Test;

public class FrameBlackboardTests
{
    private readonly record struct Grid(int TilesX, int TilesY);
    private readonly record struct ReferencedResult(object Value);

    [Test]
    public async Task typed_results_preserve_values_and_distinguish_absence_from_a_published_null()
    {
        var board = new FrameBlackboard();
        var grid = new FrameDataKey<Grid>("grid");
        var resource = new FrameDataKey<object?>("resource");
        var fallback = new Grid(1, 1);

        await Assert.That(board.TryGet(grid, out var absent)).IsFalse();
        await Assert.That(absent).IsEqualTo(default(Grid));
        await Assert.That(board.GetOrDefault(grid, fallback)).IsEqualTo(fallback);

        board.Publish(grid, new Grid(40, 24));
        board.Publish(resource, null);

        await Assert.That(board.TryGet(grid, out var current)).IsTrue();
        await Assert.That(current).IsEqualTo(new Grid(40, 24));
        await Assert.That(board.GetOrDefault(grid, fallback)).IsEqualTo(current);
        await Assert.That(board.TryGet(resource, out var publishedNull)).IsTrue();
        await Assert.That(publishedNull).IsNull();
    }

    [Test]
    public async Task duplicate_producers_are_rejected_until_clear_and_the_next_frame_can_publish_again()
    {
        var board = new FrameBlackboard();
        var key = new FrameDataKey<Grid>("grid");
        board.Publish(key, new Grid(40, 24));
        board.Publish("color", new GraphTexture(1));

        await Assert.That(() => board.Publish(key, new Grid(80, 48)))
            .Throws<InvalidOperationException>().WithMessageContaining("grid");
        await Assert.That(board.GetOrDefault(key, default)).IsEqualTo(new Grid(40, 24));

        board.Clear();
        await Assert.That(board.TryGet(key, out _)).IsFalse();
        await Assert.That(board.TryGet("color", out _)).IsFalse();
        board.Publish(key, new Grid(80, 48));
        board.Publish("color", new GraphTexture(2));

        await Assert.That(board.GetOrDefault(key, default)).IsEqualTo(new Grid(80, 48));
        await Assert.That(board.GetOrDefault("color", GraphTexture.Invalid)).IsEqualTo(new GraphTexture(2));
    }

    [Test]
    public async Task keys_with_the_same_name_have_distinct_typed_identities()
    {
        var board = new FrameBlackboard();
        var first = new FrameDataKey<int>("result");
        var second = new FrameDataKey<int>("result");
        var reference = new FrameDataKey<object>("result");
        var value = new object();
        board.Publish(first, 11);
        board.Publish(second, 22);
        board.Publish(reference, value);
        board.Publish("result", new GraphTexture(3));

        await Assert.That(board.GetOrDefault(first, -1)).IsEqualTo(11);
        await Assert.That(board.GetOrDefault(second, -1)).IsEqualTo(22);
        await Assert.That(board.TryGet(reference, out var actual)).IsTrue();
        await Assert.That(actual).IsSameReferenceAs(value);
        await Assert.That(board.GetOrDefault("result", GraphTexture.Invalid)).IsEqualTo(new GraphTexture(3));
    }

    [Test]
    public async Task clearing_retained_slots_releases_references_in_class_and_struct_results()
    {
        var board = new FrameBlackboard();
        var resource = new FrameDataKey<object>("resource");
        var data = new FrameDataKey<ReferencedResult>("data");
        var references = PublishReferences(board, resource, data);

        board.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var resourceAlive = references.Resource.TryGetTarget(out _);
        var dataAlive = references.Data.TryGetTarget(out _);
        GC.KeepAlive(board);

        await Assert.That(resourceAlive).IsFalse();
        await Assert.That(dataAlive).IsFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<object> Resource, WeakReference<object> Data) PublishReferences(
        FrameBlackboard board, FrameDataKey<object> resource, FrameDataKey<ReferencedResult> data)
    {
        var resourceValue = new object();
        var dataValue = new object();
        board.Publish(resource, resourceValue);
        board.Publish(data, new ReferencedResult(dataValue));
        return (new WeakReference<object>(resourceValue), new WeakReference<object>(dataValue));
    }

    [Test]
    public async Task struct_publication_and_lookup_allocate_nothing_after_warmup()
    {
        var board = new FrameBlackboard();
        var key = new FrameDataKey<Grid>("grid");
        ExerciseFrames(board, key, 256);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = ExerciseFrames(board, key, 4096);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(total).IsEqualTo(4096L * 4096);
        await Assert.That(allocated).IsEqualTo(0L);
    }

    private static long ExerciseFrames(FrameBlackboard board, FrameDataKey<Grid> key, int count)
    {
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            board.Clear();
            board.Publish(key, new Grid(i, i + 1));
            if (!board.TryGet(key, out var grid)) throw new InvalidOperationException("Missing frame result.");
            total += grid.TilesX + board.GetOrDefault(key, default).TilesY;
        }
        return total;
    }
}
