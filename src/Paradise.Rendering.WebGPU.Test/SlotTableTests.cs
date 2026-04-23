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
}
