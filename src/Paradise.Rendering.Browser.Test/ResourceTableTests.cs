namespace Paradise.Rendering.Browser.Test;

/// <summary>The browser backend's handle bookkeeping. Everything else in the package needs a live
/// browser (and a real GPU) and is covered by the Paradise.Rendering.Browser.Sample acceptance
/// page instead — but this part is pure managed state, and it is what makes a destroyed handle
/// throw instead of silently addressing whichever resource later takes over its JS table slot.</summary>
public class ResourceTableTests
{
    [Test]
    public async Task allocate_issues_slot_zero_at_generation_one()
    {
        var table = new ResourceTable();
        var index = table.Allocate(out var generation);
        await Assert.That(index).IsEqualTo(0u);
        // Generation 0 is the invalid sentinel on every Paradise.Rendering handle struct, so a
        // freshly issued handle must never carry it.
        await Assert.That(generation).IsEqualTo(1u);
        await Assert.That(table.IsAlive(index, generation)).IsTrue();
    }

    [Test]
    public async Task allocate_hands_out_ascending_slots_while_none_are_free()
    {
        var table = new ResourceTable();
        await Assert.That(table.Allocate(out _)).IsEqualTo(0u);
        await Assert.That(table.Allocate(out _)).IsEqualTo(1u);
        await Assert.That(table.Allocate(out _)).IsEqualTo(2u);
        await Assert.That(table.LiveCount).IsEqualTo(3);
    }

    [Test]
    public async Task released_slot_is_recycled_with_a_new_generation()
    {
        var table = new ResourceTable();
        var first = table.Allocate(out var firstGeneration);
        await Assert.That(table.Release(first, firstGeneration)).IsTrue();

        var second = table.Allocate(out var secondGeneration);
        await Assert.That(second).IsEqualTo(first);
        await Assert.That(secondGeneration).IsNotEqualTo(firstGeneration);
        // The whole point: the old handle must not resolve to the new occupant.
        await Assert.That(table.IsAlive(first, firstGeneration)).IsFalse();
        await Assert.That(table.IsAlive(second, secondGeneration)).IsTrue();
    }

    [Test]
    public async Task resolve_throws_for_a_released_handle()
    {
        var table = new ResourceTable();
        var index = table.Allocate(out var generation);
        table.Release(index, generation);

        var thrown = Assert.Throws<StaleHandleException>(() => table.Resolve(index, generation, "Buffer"));
        await Assert.That(thrown!.Message).Contains("Buffer");
    }

    [Test]
    public async Task resolve_throws_for_a_handle_that_was_never_issued()
    {
        var table = new ResourceTable();
        Assert.Throws<StaleHandleException>(() => table.Resolve(7u, 1u, "Texture"));
        await Assert.That(table.LiveCount).IsEqualTo(0);
    }

    [Test]
    public async Task double_release_is_a_no_op_rather_than_a_second_free_slot()
    {
        var table = new ResourceTable();
        var index = table.Allocate(out var generation);
        await Assert.That(table.Release(index, generation)).IsTrue();
        // A second Destroy* on the same handle must not push the slot onto the free list twice —
        // that would hand the same index to two live resources.
        await Assert.That(table.Release(index, generation)).IsFalse();

        table.Allocate(out _);
        var next = table.Allocate(out _);
        await Assert.That(next).IsEqualTo(1u);
    }

    [Test]
    public async Task live_count_tracks_allocations_and_releases()
    {
        var table = new ResourceTable();
        var a = table.Allocate(out var aGeneration);
        table.Allocate(out _);
        await Assert.That(table.LiveCount).IsEqualTo(2);
        table.Release(a, aGeneration);
        await Assert.That(table.LiveCount).IsEqualTo(1);
    }
}
