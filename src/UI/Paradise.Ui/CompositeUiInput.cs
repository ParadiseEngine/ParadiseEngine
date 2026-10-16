using Paradise.Windowing;

namespace Paradise.Ui;

/// <summary>Routes one input stream and fixed-tick updates to multiple UI systems.</summary>
/// <remarks>Button transitions stop at the first consumer in registration order; earlier inputs
/// have higher priority. Other events broadcast to all inputs and combine their consumed flags.</remarks>
public sealed class CompositeUiInput(params IUiInput[] inputs) : IUiInput
{
    public bool Handle(in WindowEvent raw)
    {
        if (raw.Kind == WindowEventKind.Button)
        {
            foreach (var input in inputs)
            {
                if (input.Handle(in raw)) return true;
            }
            return false;
        }
        // Broadcast state changes so lower-priority layers retain pointer and window state.
        var consumed = false;
        foreach (var input in inputs)
        {
            consumed |= input.Handle(in raw);
        }
        return consumed;
    }

    public void Tick(double simTimeSeconds)
    {
        foreach (var input in inputs)
        {
            input.Tick(simTimeSeconds);
        }
    }
}
