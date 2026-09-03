using Microsoft.Coyote.Specifications;
using Paradise.Ui.ImGui;

namespace Paradise.Ui.ImGui.CoyoteTest;

/// <summary>Systematic interleavings of <see cref="ImGuiTextureOps"/>, the one place in the ImGui
/// stack where two threads share mutable state.
///
/// The property under test is not "no crash" but NO LOSS AND NO REORDER: the ops queue carries a
/// state machine (create → update → destroy per texture), so a single dropped entry leaves the
/// renderer either drawing an id it never allocated or patching a texture that does not exist.
/// Both are silent in the geometry path, which is exactly why they are worth scheduling for.
///
/// These are async and await their joins so Coyote's hang detection stays a signal rather than
/// noise, per the repo's Coyote notes.</summary>
public static class TextureOpsTests
{
    private const int OpsPerProducer = 6;

    /// <summary>One producer enqueuing while the render thread drains: every op must come out,
    /// exactly once, in the order it went in.
    ///
    /// This is the test a check-then-enqueue "fast path" fails. A lost op leaves the consumer
    /// waiting for a count that never arrives, which Coyote reports as a hang.</summary>
    public static async Task DrainRacingEnqueue_LosesNothingAndKeepsOrder()
    {
        var ops = new ImGuiTextureOps();
        var observed = new List<ulong>();

        var producer = Task.Run(() =>
        {
            for (var i = 0; i < OpsPerProducer; i++)
            {
                ops.Enqueue(ImGuiTextureOp.Destroy((ulong)i));
            }
        });
        var consumer = Task.Run(() =>
        {
            var batch = new List<ImGuiTextureOp>();
            while (observed.Count < OpsPerProducer)
            {
                ops.DrainTo(batch);
                foreach (var op in batch) observed.Add(op.TextureId);
            }
        });
        await Task.WhenAll(producer, consumer).ConfigureAwait(false);

        for (var i = 0; i < OpsPerProducer; i++)
        {
            Specification.Assert(
                observed[i] == (ulong)i,
                "texture op {0} came out as {1} — the queue reordered or dropped an entry.",
                i, observed[i]);
        }
    }

    /// <summary>Two producers, because the sim thread is not the only possible source: a host
    /// that registers its own textures enqueues from wherever it runs. Each producer's own
    /// sequence must stay in order, and nothing may be lost or duplicated across the two.</summary>
    public static async Task ConcurrentProducers_KeepEachSequenceInOrder()
    {
        const ulong secondProducerBase = 1000;
        var ops = new ImGuiTextureOps();
        var observed = new List<ulong>();

        var first = Task.Run(() =>
        {
            for (var i = 0; i < OpsPerProducer; i++) ops.Enqueue(ImGuiTextureOp.Destroy((ulong)i));
        });
        var second = Task.Run(() =>
        {
            for (var i = 0; i < OpsPerProducer; i++) ops.Enqueue(ImGuiTextureOp.Destroy(secondProducerBase + (ulong)i));
        });
        var consumer = Task.Run(() =>
        {
            var batch = new List<ImGuiTextureOp>();
            while (observed.Count < OpsPerProducer * 2)
            {
                ops.DrainTo(batch);
                foreach (var op in batch) observed.Add(op.TextureId);
            }
        });
        await Task.WhenAll(first, second, consumer).ConfigureAwait(false);

        var nextFromFirst = 0ul;
        var nextFromSecond = secondProducerBase;
        foreach (var id in observed)
        {
            if (id < secondProducerBase)
            {
                Specification.Assert(id == nextFromFirst, "producer 1 op {0} arrived out of order (expected {1}).", id, nextFromFirst);
                nextFromFirst++;
            }
            else
            {
                Specification.Assert(id == nextFromSecond, "producer 2 op {0} arrived out of order (expected {1}).", id, nextFromSecond);
                nextFromSecond++;
            }
        }
        Specification.Assert(nextFromFirst == OpsPerProducer, "producer 1 lost ops: only {0} arrived.", nextFromFirst);
        Specification.Assert(
            nextFromSecond == secondProducerBase + OpsPerProducer,
            "producer 2 lost ops: only {0} arrived.", nextFromSecond - secondProducerBase);
    }

    /// <summary>A drain that races an enqueue takes a prefix, never a torn view: whatever it did
    /// not take is still pending, and the two counts always add up.</summary>
    public static async Task DrainAndPendingCount_AlwaysAccountForEveryOp()
    {
        var ops = new ImGuiTextureOps();
        var taken = 0;

        var producer = Task.Run(() =>
        {
            for (var i = 0; i < OpsPerProducer; i++) ops.Enqueue(ImGuiTextureOp.Destroy((ulong)i));
        });
        var consumer = Task.Run(() =>
        {
            var batch = new List<ImGuiTextureOp>();
            while (taken < OpsPerProducer)
            {
                taken += ops.DrainTo(batch);
                Specification.Assert(
                    taken + ops.PendingCount <= OpsPerProducer,
                    "drained {0} with {1} still pending — more ops exist than were ever enqueued.",
                    taken, ops.PendingCount);
            }
        });
        await Task.WhenAll(producer, consumer).ConfigureAwait(false);

        Specification.Assert(ops.PendingCount == 0, "{0} ops were left behind after the last drain.", ops.PendingCount);
    }
}
