using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.Test;

public class SlotTableTests
{
    private sealed class Probe { public int Tag; }

    [Test]
    public async Task add_then_get_returns_same_value()
    {
        var table = new SlotTable<Probe>();
        var p = new Probe { Tag = 42 };
        var (i, g) = table.Add(p);
        await Assert.That(g).IsEqualTo(1u);
        await Assert.That(table.TryGet(i, g, out var fetched)).IsTrue();
        await Assert.That(fetched.Tag).IsEqualTo(42);
    }

    [Test]
    public async Task remove_bumps_generation_and_invalidates_old_handle()
    {
        var table = new SlotTable<Probe>();
        var (i, g1) = table.Add(new Probe { Tag = 1 });
        await Assert.That(table.Remove(i, g1)).IsTrue();

        // Old handle no longer resolves.
        await Assert.That(table.TryGet(i, g1, out _)).IsFalse();

        // Re-allocating in the freed slot returns a strictly newer generation, and the old handle
        // still doesn't resolve to the new value (this is the use-after-free guard).
        var (i2, g2) = table.Add(new Probe { Tag = 2 });
        await Assert.That(i2).IsEqualTo(i);
        await Assert.That(g2).IsGreaterThan(g1);
        await Assert.That(table.TryGet(i, g1, out _)).IsFalse();
        await Assert.That(table.TryGet(i, g2, out var fetched)).IsTrue();
        await Assert.That(fetched.Tag).IsEqualTo(2);
    }

    [Test]
    public async Task count_reflects_live_entries()
    {
        var table = new SlotTable<Probe>();
        await Assert.That(table.Count).IsEqualTo(0);
        var (_, g0) = table.Add(new Probe());
        var (i1, g1) = table.Add(new Probe());
        await Assert.That(table.Count).IsEqualTo(2);
        table.Remove(i1, g1);
        await Assert.That(table.Count).IsEqualTo(1);
        _ = g0;
    }

    [Test]
    public async Task detach_returns_value_and_invalidates_old_handle()
    {
        // Device-free coverage for the synchronous-detach contract. WebGpuRenderer.Destroy*()
        // paths are exercised via HandleDistinctnessTests but those are Skip'd on GPU-less CI —
        // this test pins the core invariant in the standard unit suite.
        var table = new SlotTable<Probe>();
        var p = new Probe { Tag = 7 };
        var (i, g) = table.Add(p);

        // Detach hands the value back atomically.
        await Assert.That(table.Detach(i, g, out var detached)).IsTrue();
        await Assert.That(ReferenceEquals(detached, p)).IsTrue();

        // The old handle no longer resolves — same use-after-free guard as Remove.
        await Assert.That(table.TryGet(i, g, out _)).IsFalse();

        // Re-allocating in the freed slot produces a strictly newer generation.
        var (i2, g2) = table.Add(new Probe { Tag = 8 });
        await Assert.That(i2).IsEqualTo(i);
        await Assert.That(g2).IsGreaterThan(g);
        await Assert.That(table.TryGet(i, g, out _)).IsFalse();
    }

    [Test]
    public async Task detach_then_count_reports_zero()
    {
        // Regression for the iteration-4 OpenCara finding that claimed Detach forgot to decrement
        // a _count field. SlotTable.Count is actually derived — `_slots.Count - _free.Count` —
        // and both Remove and Detach push onto _free, so the counter is consistent across paths.
        // This test pins that invariant so any future refactor that introduces a stored _count
        // field won't reintroduce the drift the finding imagined.
        var table = new SlotTable<Probe>();
        var (i, g) = table.Add(new Probe());
        await Assert.That(table.Count).IsEqualTo(1);
        await Assert.That(table.Detach(i, g, out _)).IsTrue();
        await Assert.That(table.Count).IsEqualTo(0);
    }

    [Test]
    public async Task detach_with_stale_handle_returns_false()
    {
        // Double-detach / wrong-generation / out-of-range handles all return false without
        // mutating the table. Mirrors Remove's stale-handle semantics.
        var table = new SlotTable<Probe>();
        var (i, g) = table.Add(new Probe());

        await Assert.That(table.Detach(i, g, out _)).IsTrue();
        await Assert.That(table.Detach(i, g, out _)).IsFalse();
        await Assert.That(table.Detach(i, g + 1u, out _)).IsFalse();
        await Assert.That(table.Detach(99u, 1u, out _)).IsFalse();
    }
}
