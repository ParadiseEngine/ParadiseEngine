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
    /// <summary>Process one input event. Returns true when the UI consumed it (e.g. the pointer
    /// hit something that handles input) — consumed presses do not reach game logic.
    ///
    /// Read the verdict as HANDLED, not HIT: an implementation over a retained-mode toolkit
    /// reports what an element actually took, and a hit-testable element that handles nothing
    /// (a bare panel, a rectangle) returns false. A host blocking input on this must put
    /// something that handles input under the pointer.</summary>
    bool Handle(in WindowEvent input);

    /// <summary>Advance UI time (fires animations, bindings, layout). Called once per fixed
    /// simulation tick with canonical sim time, on the same thread as <see cref="Handle"/>.</summary>
    void Tick(double simTimeSeconds);
}
