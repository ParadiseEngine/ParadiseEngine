using Microsoft.Coyote.Specifications;
using Paradise.Ui.ImGui;

namespace Paradise.Ui.ImGui.CoyoteTest;

/// <summary>Systematic interleavings of <see cref="ImGuiFrameExchange"/>, the sim → render handoff.
///
/// The property under test is that <b>a snapshot never reaches the renderer ahead of the texture
/// ops it depends on</b>. That is not a memory-safety property and no stress loop reliably finds
/// its violation: it needs the sim thread to publish a whole new frame in the window between the
/// render thread's two steps, which is one specific interleaving out of many. Reverse the two
/// lines inside <c>AcquireForRender</c> — drain the ops, then swap the snapshot — and these tests
/// fail; that is the defect they were written against.
///
/// Async with awaited joins so Coyote's hang detection stays meaningful, per the repo's notes.</summary>
public static class FrameExchangeTests
{
    private const int Frames = 4;

    /// <summary>Every snapshot the render thread draws names only textures whose Create op it has
    /// already been handed.
    ///
    /// The model is one frame per texture: frame <c>f</c> enqueues a Create for texture <c>f</c>
    /// and publishes a snapshot whose single command samples it. Frames may be dropped — that is
    /// the point of the snapshot buffer — but a snapshot that IS drawn must have its texture.</summary>
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

    /// <summary>A snapshot handed to the render thread is never simultaneously back in the free
    /// pool: the sim thread must not be able to rent and overwrite the very buffer being drawn.
    ///
    /// Modelled by a tear marker — the sim writes <c>CommandCount = 0</c>, fills the commands, then
    /// sets the real count. A render thread that ever observes the intermediate state has been
    /// given a snapshot the sim still owns.</summary>
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
