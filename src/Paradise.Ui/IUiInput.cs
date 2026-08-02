namespace Paradise.Ui;

/// <summary>The SIMULATION-thread half of a UI system. The renderer half lives on the render
/// thread (it owns GPU resources and draws the composited overlay); this half owns interaction:
/// the simulation drains queued <see cref="UiEvent"/>s into <see cref="Handle"/> and then calls
/// <see cref="Tick"/> once per fixed tick, all on the sim thread, so UI state (hover, focus,
/// animations, bindings) advances in lockstep with game state. Implementations synchronize
/// internally with their renderer half (e.g. Noesis's view-update vs render-tree-update
/// handoff).</summary>
public interface IUiInput
{
    /// <summary>Process one input event on the sim thread. Returns true when the UI consumed
    /// it (e.g. the pointer hit a control) — consumed pointer-downs do not reach game logic.</summary>
    bool Handle(in UiEvent uiEvent);

    /// <summary>Advance UI time on the sim thread (fires animations, bindings, layout).
    /// Called once per fixed simulation tick with canonical sim time.</summary>
    void Tick(double simTimeSeconds);
}
