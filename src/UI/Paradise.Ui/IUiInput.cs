using Paradise.Windowing;

namespace Paradise.Ui;

/// <summary>Handles window input and fixed-tick UI updates on the simulation thread.</summary>
/// <remarks>Hosts choose which WindowEvents reach Handle, then call Tick on the same thread. Keep
/// Resize in event order so later pointer events use the new geometry; implementations synchronize
/// their renderer handoff.</remarks>
public interface IUiInput
{
    /// <summary>Processes an event and reports whether the UI consumes it.</summary>
    /// <remarks>Pointer presses report an input hit, including a panel background; Noesis 4.0.0
    /// cannot reliably report handling for presses. Other events use the toolkit's handled result.
    /// For click-through overlays, use null root backgrounds and IsHitTestVisible=false on
    /// decoration.</remarks>
    bool Handle(in WindowEvent input);

    /// <summary>Advance UI time (fires animations, bindings, layout). Called once per fixed
    /// simulation tick with canonical sim time, on the same thread as <see cref="Handle"/>.</summary>
    void Tick(double simTimeSeconds);
}
