using System.Collections.Immutable;

namespace Paradise.ECS.Test;

/// <summary>
/// FreshReadMask scheduling ([CurrentTick] fresh reads): a system whose write set overlaps
/// another system's fresh-read set must land in an EARLIER wave, in both DAG schedulers.
/// Metadata-level tests — no generated systems required.
/// </summary>
public sealed class FreshReadDagSchedulerTests
{
    private static SystemMetadata<SmallBitSet<uint>> Meta(
        int systemId,
        SmallBitSet<uint> readMask = default,
        SmallBitSet<uint> writeMask = default,
        SmallBitSet<uint> freshReadMask = default)
    {
        return new SystemMetadata<SmallBitSet<uint>>
        {
            SystemId = systemId,
            TypeName = $"System{systemId}",
            ReadMask = readMask,
            WriteMask = writeMask,
            FreshReadMask = freshReadMask,
            AfterSystemIds = ImmutableArray<int>.Empty,
        };
    }

    private static SmallBitSet<uint> Bit(int index) => SmallBitSet<uint>.Empty.Set(index);

    private static int[] WaveMap(int[][] waves, int systemCount)
    {
        var map = new int[systemCount];
        for (int w = 0; w < waves.Length; w++)
            foreach (var idx in waves[w])
                map[idx] = w;
        return map;
    }

    [Test]
    public async Task snapshot_scheduler_plain_read_shares_wave_but_fresh_read_splits()
    {
        var writer = Meta(0, readMask: Bit(0), writeMask: Bit(0));
        var plainReader = Meta(1, readMask: Bit(0));
        var freshReader = Meta(2, readMask: Bit(0), freshReadMask: Bit(0));

        int[][] plainWaves = new SnapshotDagScheduler().ComputeWaves<SmallBitSet<uint>>([writer, plainReader]);
        await Assert.That(plainWaves.Length).IsEqualTo(1); // snapshot reads never alias writes

        int[][] freshWaves = new SnapshotDagScheduler().ComputeWaves<SmallBitSet<uint>>([writer, freshReader]);
        await Assert.That(freshWaves.Length).IsEqualTo(2); // fresh read aliases the write
    }

    [Test]
    public async Task snapshot_scheduler_orders_fresh_reader_after_writer_regardless_of_add_order()
    {
        // Fresh reader listed FIRST — without the implicit writer → reader edge, greedy wave
        // assignment would place it in wave 0 and bump the writer instead (wrong direction).
        var freshReader = Meta(0, readMask: Bit(0), freshReadMask: Bit(0));
        var writer = Meta(1, readMask: Bit(0), writeMask: Bit(0));

        int[][] waves = new SnapshotDagScheduler().ComputeWaves<SmallBitSet<uint>>([freshReader, writer]);
        var waveOf = WaveMap(waves, 2);
        await Assert.That(waveOf[0]).IsGreaterThan(waveOf[1]); // reader strictly after writer
    }

    [Test]
    public async Task default_scheduler_orders_fresh_reader_after_writer_regardless_of_add_order()
    {
        var freshReader = Meta(0, readMask: Bit(0), freshReadMask: Bit(0));
        var writer = Meta(1, readMask: Bit(0), writeMask: Bit(0));

        int[][] waves = new DefaultDagScheduler().ComputeWaves<SmallBitSet<uint>>([freshReader, writer]);
        var waveOf = WaveMap(waves, 2);
        await Assert.That(waveOf[0]).IsGreaterThan(waveOf[1]);
    }

    [Test]
    public async Task fresh_read_without_overlapping_writer_adds_no_edges()
    {
        var freshReader = Meta(0, readMask: Bit(0), freshReadMask: Bit(0));
        var unrelatedWriter = Meta(1, readMask: Bit(1), writeMask: Bit(1));

        int[][] waves = new SnapshotDagScheduler().ComputeWaves<SmallBitSet<uint>>([freshReader, unrelatedWriter]);
        await Assert.That(waves.Length).IsEqualTo(1);
    }

    [Test]
    public async Task mutual_fresh_read_write_pairs_are_a_cycle()
    {
        // A writes 0 and fresh-reads 1; B writes 1 and fresh-reads 0 — each must run after the
        // other, which is unsatisfiable.
        var a = Meta(0, readMask: Bit(0).Set(1), writeMask: Bit(0), freshReadMask: Bit(1));
        var b = Meta(1, readMask: Bit(0).Set(1), writeMask: Bit(1), freshReadMask: Bit(0));

        await Assert.That(() => new SnapshotDagScheduler().ComputeWaves<SmallBitSet<uint>>([a, b]))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task default_metadata_without_fresh_reads_is_unchanged()
    {
        // Pre-existing metadata never sets FreshReadMask — the default must add no edges.
        SystemMetadata<SmallBitSet<uint>>[] systems =
        [
            new SystemMetadata<SmallBitSet<uint>> { SystemId = 0, TypeName = "A" },
            new SystemMetadata<SmallBitSet<uint>> { SystemId = 1, TypeName = "B" },
        ];
        int[][] waves = new SnapshotDagScheduler().ComputeWaves<SmallBitSet<uint>>(systems);
        await Assert.That(waves.Length).IsEqualTo(1);
    }
}
