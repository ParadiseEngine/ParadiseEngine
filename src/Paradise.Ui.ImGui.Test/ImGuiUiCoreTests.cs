using System;
using System.Collections.Generic;
using ImGuiApi = Hexa.NET.ImGui.ImGui;
using Paradise.Windowing;

namespace Paradise.Ui.ImGui.Test;

/// <summary>The promoted two-half core, driven the way a host drives it: events in on the sim
/// half, snapshots and texture ops out on the render half.
///
/// <c>ImGuiUiCore</c> creates a context and leaves it current for the process, so these run
/// serialized with every other ImGui test and each one starts from a context of its own.</summary>
[NotInParallel]
public class ImGuiUiCoreTests
{
    private const uint Width = 320;
    private const uint Height = 240;

    private static ImGuiUiCore NewCore(Action draw)
    {
        var core = new ImGuiUiCore(Width, Height);
        core.AddDraw(draw);
        return core;
    }

    [Test]
    public async Task first_tick_publishes_a_snapshot_and_the_font_atlas()
    {
        using var core = NewCore(() => ImGuiTestContext.Panel("hello"));
        var ops = new List<ImGuiTextureOp>();

        await Assert.That(core.AcquireSnapshotForRender(ops, out _)).IsNull();

        core.Input.Tick(0.0);
        var snapshot = core.AcquireSnapshotForRender(ops, out var isNew);

        await Assert.That(snapshot).IsNotNull();
        await Assert.That(isNew).IsTrue();
        await Assert.That(snapshot!.CommandCount).IsGreaterThan(0);
        // The atlas has to arrive with the first frame, or the first draw samples nothing.
        await Assert.That(ops.Count).IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(ImGuiTextureOpKind.Create);
        await Assert.That(snapshot.Commands[0].TextureId).IsEqualTo(ops[0].TextureId);
    }

    [Test]
    public async Task acquiring_twice_without_a_tick_reports_the_same_snapshot()
    {
        using var core = NewCore(() => ImGuiTestContext.Panel("hello"));
        var ops = new List<ImGuiTextureOp>();

        core.Input.Tick(0.0);
        var first = core.AcquireSnapshotForRender(ops, out var firstIsNew);
        var second = core.AcquireSnapshotForRender(ops, out var secondIsNew);

        await Assert.That(firstIsNew).IsTrue();
        await Assert.That(secondIsNew).IsFalse();
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        // Ops are drained from the QUEUE, so the second acquire adds nothing. They stay in the
        // list until something applies them — re-delivering one would re-upload a whole atlas.
        await Assert.That(ops.Count).IsEqualTo(1);
    }

    /// <summary>A host that acquires a frame and then does not render it must not lose the ops it
    /// was handed: the drain already took them off the queue, so the list is the only copy.</summary>
    [Test]
    public async Task ops_survive_a_frame_the_host_acquired_and_never_rendered()
    {
        var text = "hello";
        using var core = NewCore(() => ImGuiTestContext.Panel(text));
        var ops = new List<ImGuiTextureOp>();

        core.Input.Tick(0.0);
        core.AcquireSnapshotForRender(ops, out _); // acquired... and the host renders nothing

        text = "XYZ@#";
        core.Input.Tick(1.0 / 60.0);
        core.AcquireSnapshotForRender(ops, out _);

        // Both the atlas and the glyphs added since are still waiting, in order.
        await Assert.That(ops.Count).IsEqualTo(2);
        await Assert.That(ops[0].Kind).IsEqualTo(ImGuiTextureOpKind.Create);
        await Assert.That(ops[1].Kind).IsEqualTo(ImGuiTextureOpKind.Update);
    }

    [Test]
    public async Task new_glyphs_arrive_as_an_update_op_on_a_later_tick()
    {
        var text = "hello";
        using var core = NewCore(() => ImGuiTestContext.Panel(text));
        var ops = new List<ImGuiTextureOp>();

        core.Input.Tick(0.0);
        core.AcquireSnapshotForRender(ops, out _);
        await Assert.That(ops[0].Kind).IsEqualTo(ImGuiTextureOpKind.Create);
        ops.Clear(); // stands in for ApplyTextureOps, which is what clears in a real host

        text = "XYZ@#";
        core.Input.Tick(1.0 / 60.0);
        core.AcquireSnapshotForRender(ops, out _);

        await Assert.That(ops.Count).IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(ImGuiTextureOpKind.Update);
    }

    [Test]
    public async Task a_pointer_over_a_window_is_consumed_and_elsewhere_is_not()
    {
        using var core = NewCore(() => ImGuiTestContext.Panel("hello"));
        var ops = new List<ImGuiTextureOp>();
        // WantCaptureMouse is decided at NewFrame against the PREVIOUS frame.s windows, so a
        // move needs two ticks before its verdict means anything: one to create the window,
        // one to hit-test the pointer against it.
        core.Input.Handle(WindowEvent.PointerMove(100, 60));
        core.Input.Tick(0.0);
        core.Input.Tick(1.0 / 60.0);
        core.AcquireSnapshotForRender(ops, out _);

        var overWindow = core.Input.Handle(WindowEvent.Mouse(PointerButton.Left, true, 100, 60));

        core.Input.Handle(WindowEvent.Mouse(PointerButton.Left, false, 100, 60));
        core.Input.Handle(WindowEvent.PointerMove(300, 220));
        // ImGui trickles its input queue (ConfigInputTrickleEventQueue): a frame takes at most
        // one position change and will not mix it with a button change, so the release and the
        // move away land on separate frames before the pointer is anywhere new.
        for (var tick = 2; tick < 5; tick++) core.Input.Tick(tick / 60.0);
        var overNothing = core.Input.Handle(WindowEvent.Mouse(PointerButton.Left, true, 300, 220));

        await Assert.That(overWindow).IsTrue();
        await Assert.That(overNothing).IsFalse();
    }

    [Test]
    public async Task unmapped_input_is_left_to_the_game()
    {
        using var core = NewCore(() => ImGuiTestContext.Panel("hello"));
        core.Input.Tick(0.0);

        // Neither has an ImGui meaning, and the core must not claim what it did not use — a
        // false consumption withholds the input from game logic.
        await Assert.That(core.Input.Handle(WindowEvent.KeyDownOf(KeyboardKey.None))).IsFalse();
        await Assert.That(core.Input.Handle(WindowEvent.Axis(GamepadAxis.LeftX, 0.5f))).IsFalse();
    }

    [Test]
    public async Task a_resize_event_moves_the_display_rect()
    {
        using var core = NewCore(() => ImGuiTestContext.Panel("hello"));
        var ops = new List<ImGuiTextureOp>();

        core.Input.Handle(WindowEvent.Resize(640, 480));
        core.Input.Tick(0.0);
        var snapshot = core.AcquireSnapshotForRender(ops, out _);

        await Assert.That(snapshot!.DisplaySize.X).IsEqualTo(640f);
        await Assert.That(snapshot.DisplaySize.Y).IsEqualTo(480f);
    }

    [Test]
    public async Task clipboard_text_round_trips_between_the_host_and_the_ui()
    {
        using var core = NewCore(() => ImGuiTestContext.Panel("hello"));

        await Assert.That(core.TryTakeClipboardCopy(out _)).IsFalse();
        core.SetHostClipboard("from the host");
        // Nothing was copied IN the UI, so there is still nothing for the host to publish.
        await Assert.That(core.TryTakeClipboardCopy(out _)).IsFalse();
    }

    /// <summary>The clipboard bridge driven through ImGui's OWN entry points, so the call crosses
    /// the <c>[UnmanagedCallersOnly]</c> trampolines installed into <c>ImGuiPlatformIO</c>.
    ///
    /// The test above exercises only the managed cache — both halves of it are our own methods —
    /// so it passes whether or not those function pointers ever reach cimgui. A field-offset
    /// mismatch in Hexa's <c>ImGuiPlatformIO</c>, or a marshalling mistake in either trampoline,
    /// would leave the UI unable to copy or paste and ship green.</summary>
    [Test]
    public async Task the_clipboard_bridge_is_reached_through_native_imgui()
    {
        using var core = NewCore(() => ImGuiTestContext.Panel("hello"));

        // Copy: cimgui calls out through Platform_SetClipboardTextFn.
        ImGuiApi.SetClipboardText("copied in the ui");
        await Assert.That(core.TryTakeClipboardCopy(out var copied)).IsTrue();
        await Assert.That(copied).IsEqualTo("copied in the ui");

        // Paste: the host publishes the system clipboard, cimgui asks for it through
        // Platform_GetClipboardTextFn.
        core.SetHostClipboard("from the host");
        await Assert.That(ImGuiApi.GetClipboardTextS()).IsEqualTo("from the host");
    }

    /// <summary>Non-ASCII both ways: the trampolines marshal UTF-8, and a bridge that only ever
    /// saw ASCII would hide a length-vs-byte-count mistake.</summary>
    [Test]
    public async Task the_clipboard_bridge_carries_utf8()
    {
        using var core = NewCore(() => ImGuiTestContext.Panel("hello"));

        ImGuiApi.SetClipboardText("复制的文本");
        await Assert.That(core.TryTakeClipboardCopy(out var copied)).IsTrue();
        await Assert.That(copied).IsEqualTo("复制的文本");

        core.SetHostClipboard("粘贴的文本");
        await Assert.That(ImGuiApi.GetClipboardTextS()).IsEqualTo("粘贴的文本");
    }
}
