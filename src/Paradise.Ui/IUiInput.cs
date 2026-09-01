using Paradise.Windowing;

namespace Paradise.Ui;

/// <summary>The SIMULATION-thread half of a UI system. The renderer half lives on the render
/// thread (it owns GPU resources and draws the composited overlay); this half owns interaction:
/// the host drains <see cref="WindowEvent"/> into <see cref="Handle"/> and then calls
/// <see cref="Tick"/> once per fixed tick, all on one thread, so UI state (hover, focus,
/// animations, bindings) advances in lockstep with game state. Implementations synchronize
/// internally with their renderer half (e.g. Noesis's view-update vs render-tree-update
/// handoff).
///
/// <b>It consumes the window's own vocabulary.</b> There is no UI-specific event type to
/// translate into, which is deliberate: a parallel type had to mirror every device the window
/// could report, and the two drifted — the UI could describe a scroll and a keystroke but never
/// a touch, because nobody remembered to add one. A host now forwards what it already has, and
/// the only thing left for it to decide is WHICH events its UI should see. That decision is
/// real and stays with the host: a game that forwards [W] has handed movement to whatever holds
/// focus.
///
/// <see cref="WindowEventKind.Resize"/> rides the same stream rather than a side channel, so a UI
/// applies a size change at the same point in the sequence the pointer events do — a resize
/// handled out of band hit-tests the next click against the old geometry.</summary>
public interface IUiInput
{
    /// <summary>Process one input event. Returns true when the UI consumed it — consumed presses
    /// do not reach game logic.
    ///
    /// <b>A PRESS answers HIT: was anything the UI routes input to under the pointer.</b> Not
    /// "did an element run a handler" — a bare panel with a background consumes a press even
    /// though nothing would have run. That is the honest contract rather than the preferred one:
    /// Noesis 4.0.0's <c>View.MouseButtonDown</c> returns true whatever is beneath the pointer,
    /// including over an empty view, so an implementation cannot report what an element actually
    /// took and one that claimed to would be lying. A UI that wants clicks to fall through
    /// therefore has to be AUTHORED to: null backgrounds on roots, and
    /// <c>IsHitTestVisible="False"</c> on paint. Hit-testability is the switch, not handler
    /// presence.
    ///
    /// Every other kind still answers HANDLED, from the toolkit's own verdict: a move, a
    /// release or a wheel over a panel nothing listens to returns false. The split is not a
    /// design — it is where one toolkit stopped being able to tell us, and it is written down
    /// here so a host is not surprised by it.</summary>
    bool Handle(in WindowEvent input);

    /// <summary>Advance UI time (fires animations, bindings, layout). Called once per fixed
    /// simulation tick with canonical sim time, on the same thread as <see cref="Handle"/>.</summary>
    void Tick(double simTimeSeconds);
}
