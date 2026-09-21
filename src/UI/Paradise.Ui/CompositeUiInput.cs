using Paradise.Windowing;

namespace Paradise.Ui;

/// <summary>Fan-out for running several UI systems on one input stream (e.g. ImGui debug panels
/// over Noesis game UI). Button transitions stop at the first consumer in registration order
/// (earlier = higher priority); everything else broadcasts to all.</summary>
public sealed class CompositeUiInput(params IUiInput[] inputs) : IUiInput
{
    public bool Handle(in WindowEvent raw)
    {
        // A press or release goes to ONE consumer — whoever takes it, owns it. Everything else
        // (moves, scrolls, the resize) broadcasts, because more than one layer legitimately
        // needs to know where the pointer is and how big the window got.
        if (raw.Kind == WindowEventKind.Button)
        {
            foreach (var input in inputs)
            {
                if (input.Handle(in raw)) return true;
            }
            return false;
        }
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
