using System.Numerics;

namespace Paradise.Rendering.Pbr.Test;

public class ProbeSchedulingTests
{
    [Test]
    public async Task scrolling_preserves_world_positions_of_every_overlapping_storage_slot()
    {
        var grid = new ProbeGrid();
        var initial = new PbrProbeVolume(new Vector3(-3, -2, -5), new Vector3(2, 3, 4), 5, 4, 3);
        grid.SetVolume(initial, true);
        foreach (var step in new[] { new Vector3(1, 0, 0), new Vector3(-2, 1, 0), new Vector3(0, -2, 1), new Vector3(3, 1, -2), new Vector3(-1, 0, 1) })
        {
            var before = Enumerable.Range(0, grid.Count).Select(grid.Position).ToArray();
            var previous = grid.Volume!;
            await Assert.That(grid.SetVolume(previous with { Origin = previous.Origin + step * previous.Spacing }, true)).IsFalse();
            var reset = grid.ResetIndices.ToArray().ToHashSet();
            var expectedReset = grid.Count - (previous.CountX - Math.Abs((int)step.X))
                * (previous.CountY - Math.Abs((int)step.Y)) * (previous.CountZ - Math.Abs((int)step.Z));
            await Assert.That(reset.Count).IsEqualTo(expectedReset);
            var usedSlots = new HashSet<int>();
            for (var z = 0; z < previous.CountZ; z++)
            for (var y = 0; y < previous.CountY; y++)
            for (var x = 0; x < previous.CountX; x++)
            {
                var index = grid.Index(x, y, z);
                await Assert.That(usedSlots.Add(index)).IsTrue();
                await Assert.That(grid.Position(index)).IsEqualTo(grid.Volume!.Origin + new Vector3(x, y, z) * previous.Spacing);
                if (!reset.Contains(index)) await Assert.That(grid.Position(index)).IsEqualTo(before[index]);
            }
        }
    }

    [Test]
    public async Task sub_cell_motion_accumulates_and_a_teleport_or_spacing_change_resets_the_grid()
    {
        var grid = new ProbeGrid();
        var volume = new PbrProbeVolume(Vector3.Zero, Vector3.One, 3, 4, 5);
        grid.SetVolume(volume, true);
        grid.SetVolume(volume with { Origin = new Vector3(-0.9f, 0.9f, 0) }, true);
        await Assert.That(grid.Volume).IsEqualTo(volume);
        await Assert.That(grid.ResetCount).IsEqualTo(0);
        grid.SetVolume(volume with { Origin = new Vector3(-1.1f, 1.1f, 0) }, true);
        await Assert.That(grid.Volume!.Origin).IsEqualTo(new Vector3(-1, 1, 0));
        await Assert.That(grid.SetVolume(volume with { Origin = new Vector3(99.9f, -82.2f, 3) }, true)).IsTrue();
        await Assert.That(grid.ResetCount).IsEqualTo(grid.Count);
        await Assert.That(grid.Scroll).IsEqualTo((0, 0, 0));
        await Assert.That(grid.SetVolume(grid.Volume! with { Spacing = new Vector3(1.001f) }, true)).IsTrue();
        await Assert.That(grid.ResetCount).IsEqualTo(grid.Count);
    }

    [Test]
    public async Task focus_updates_are_unique_and_leave_a_fair_background_sweep_even_with_a_single_slot()
    {
        var grid = new ProbeGrid();
        grid.SetVolume(new PbrProbeVolume(Vector3.Zero, Vector3.One, 3, 3, 3), true);
        var scheduler = new ProbeUpdateScheduler();
        scheduler.Reset(grid.Count);
        scheduler.Select(grid, 0, null);
        var visited = new HashSet<int>();
        for (var frame = 0; frame < grid.Count * 2; frame++)
        {
            await Assert.That(scheduler.Select(grid, 1, Vector3.Zero)).IsEqualTo(1);
            visited.Add(scheduler.Updates[0].Probe);
        }
        await Assert.That(visited.Count).IsEqualTo(grid.Count);

        scheduler.Select(grid, 6, Vector3.Zero);
        var updates = scheduler.Updates.ToArray();
        await Assert.That(updates.Take(6).Select(u => u.Probe).Distinct().Count()).IsEqualTo(6);
        await Assert.That(updates.Take(6).Any(u => u.Probe == grid.Index(0, 0, 0))).IsTrue();
        for (var slot = 0; slot < 6; slot++)
            await Assert.That(updates[updates[slot].Probe].Slot).IsEqualTo(slot);
        await Assert.That(updates.Count(u => u.Slot < 0)).IsEqualTo(grid.Count - 6);
    }

    [Test]
    public async Task newly_entered_planes_and_invalidated_distant_probes_precede_focus_updates()
    {
        var grid = new ProbeGrid();
        grid.SetVolume(new PbrProbeVolume(Vector3.Zero, Vector3.One, 3, 3, 3), true);
        var scheduler = new ProbeUpdateScheduler();
        scheduler.Reset(grid.Count);
        scheduler.Select(grid, 0, null);
        grid.SetVolume(grid.Volume! with { Origin = Vector3.UnitX }, true);
        var reset = grid.ResetIndices.ToArray().ToHashSet();
        foreach (var probe in reset) scheduler.Invalidate(probe);
        var selected = new HashSet<int>();
        for (var frame = 0; frame < 3; frame++)
        {
            scheduler.Select(grid, 3, Vector3.Zero);
            for (var slot = 0; slot < 3; slot++) selected.Add(scheduler.Updates[slot].Probe);
        }
        await Assert.That(selected.SetEquals(reset)).IsTrue();
        var distant = grid.Index(2, 2, 2);
        scheduler.Invalidate(distant);
        scheduler.Select(grid, 1, Vector3.Zero);
        await Assert.That(scheduler.Updates[0].Probe).IsEqualTo(distant);
    }

    [Test]
    public async Task authored_nonfinite_and_overflowing_grids_are_rejected_before_allocation()
    {
        var settings = new PbrGi { MaxProbes = int.MaxValue };
        await Assert.That(() => ProbeGiFeature.ValidateAuthored(new PbrProbeVolume(Vector3.Zero, Vector3.One, int.MaxValue, int.MaxValue, int.MaxValue), settings))
            .Throws<ArgumentException>().WithMessageContaining("MaxProbes");
        await Assert.That(() => ProbeGiFeature.ValidateAuthored(new PbrProbeVolume(new Vector3(float.NaN), Vector3.One, 2, 2, 2), settings))
            .Throws<ArgumentException>().WithMessageContaining("finite");
        await Assert.That(() => ProbeGiFeature.ValidateAuthored(new PbrProbeVolume(Vector3.Zero, new Vector3(float.PositiveInfinity), 2, 2, 2), settings))
            .Throws<ArgumentException>().WithMessageContaining("finite");
    }
}
