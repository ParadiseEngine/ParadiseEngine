using Microsoft.Coyote.Specifications;
using Paradise.Ui.ImGui;

namespace Paradise.Ui.ImGui.CoyoteTest;

/// <summary>Checks that acquired snapshots never precede their texture operations.</summary>
/// <remarks>Reversing the swap and drain in AcquireForRender makes these Coyote tests fail; joins
/// are awaited so hang detection remains effective.</remarks>
public static class FrameExchangeTests
{
    private const int Frames = 4;

    /// <summary>Every drawn snapshot must reference textures whose Create operations have
    /// arrived.</summary>
    /// <remarks>Each frame creates one texture and samples it; superseded snapshots may be
    /// dropped.</remarks>
    public static async Task DrawnSnapshotsNeverNameAnUncreatedTexture()
    {
        var exchange = new ImGuiFrameExchange();
        var created = new HashSet<ulong>();
        var drawn = 0;

        Task sim = null!;
        sim = Task.Run(() =>
        {
            for (var frame = 0; frame < Frames; frame++)
            {
                var id = (ulong)frame + 1;
                exchange.TextureOps.Enqueue(ImGuiTextureOp.Create(id, 1, 1, new byte[4]));
                var snapshot = exchange.Rent();
                snapshot.CommandCount = 1;
                if (snapshot.Commands.Length == 0) snapshot.Commands = new ImGuiDrawSnapshot.Command[1];
                snapshot.Commands[0] = new ImGuiDrawSnapshot.Command(default, id, 0, 0, 3);
                exchange.Publish(snapshot);
            }
        });
        var render = Task.Run(() =>
        {
            var ops = new List<ImGuiTextureOp>();
            // Frames may be dropped, so there is no count to wait for. Run until the sim is done
            // AND at least one snapshot has been drawn — otherwise a schedule that runs the whole
            // render loop before the first publish would check nothing and call it a pass.
            while (drawn == 0 || !sim.IsCompleted)
            {
                ops.Clear(); // ditto: ApplyTextureOps is what clears in a real host
                var snapshot = exchange.AcquireForRender(ops, out _);
                foreach (var op in ops)
                {
                    if (op.Kind == ImGuiTextureOpKind.Create) created.Add(op.TextureId);
                }
                if (snapshot is null || snapshot.CommandCount == 0) continue;
                drawn++;
                var id = snapshot.Commands[0].TextureId;
                Specification.Assert(
                    created.Contains(id),
                    "drew a snapshot naming texture {0} before its Create op was applied — the ops were drained before the snapshot swap.",
                    id);
            }
        });
        await Task.WhenAll(sim, render).ConfigureAwait(false);

        Specification.Assert(drawn > 0, "the render loop never drew anything, so nothing was checked.");
    }

    /// <summary>A snapshot being rendered must never return to the producer pool.</summary>
    /// <remarks>The producer clears CommandCount while writing; observing that marker on the render
    /// thread detects a torn snapshot.</remarks>
    public static async Task RecycledSnapshotsAreNeverHandedOutWhileBeingWritten()
    {
        var exchange = new ImGuiFrameExchange();

        var sim = Task.Run(() =>
        {
            for (var frame = 0; frame < Frames; frame++)
            {
                var snapshot = exchange.Rent();
                snapshot.CommandCount = 0;
                if (snapshot.Commands.Length == 0) snapshot.Commands = new ImGuiDrawSnapshot.Command[1];
                snapshot.Commands[0] = new ImGuiDrawSnapshot.Command(default, (ulong)frame + 1, 0, 0, 3);
                snapshot.CommandCount = 1;
                exchange.Publish(snapshot);
            }
        });
        var render = Task.Run(() =>
        {
            var ops = new List<ImGuiTextureOp>();
            for (var pass = 0; pass < Frames * 2; pass++)
            {
                ops.Clear();
                var snapshot = exchange.AcquireForRender(ops, out var isNew);
                if (snapshot is null) continue;
                Specification.Assert(
                    snapshot.CommandCount == 1,
                    "acquired a snapshot mid-write (CommandCount {0}) — the pool handed out a buffer the sim thread still owns.",
                    snapshot.CommandCount);
                Specification.Assert(
                    !isNew || snapshot.Commands[0].ElementCount == 3,
                    "acquired a snapshot whose commands were not fully written.");
            }
        });
        await Task.WhenAll(sim, render).ConfigureAwait(false);
    }
}
