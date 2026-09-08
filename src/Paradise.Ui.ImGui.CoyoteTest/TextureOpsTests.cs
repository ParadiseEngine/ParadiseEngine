using Microsoft.Coyote.Specifications;
using Paradise.Ui.ImGui;

namespace Paradise.Ui.ImGui.CoyoteTest;

/// <summary>Checks that concurrent texture queue operations preserve every entry in
/// order.</summary>
/// <remarks>Create, update and destroy form a state machine; losing or reordering an entry
/// invalidates later draws. Awaited joins preserve Coyote hang detection.</remarks>
public static class TextureOpsTests
{
    private const int OpsPerProducer = 6;

    /// <summary>Concurrent enqueue and drain must deliver every operation exactly once in
    /// order.</summary>
    /// <remarks>A lost operation leaves the consumer waiting, which Coyote reports as a
    /// hang.</remarks>
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
                batch.Clear(); // the renderer clears what it applied; stand in for that
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
                batch.Clear(); // the renderer clears what it applied; stand in for that
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
                batch.Clear();
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
