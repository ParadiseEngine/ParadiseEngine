namespace Paradise.Ui;

/// <summary>Fan-out for running several UI systems on one sim input stream (e.g. ImGui debug
/// panels over Noesis game UI). Pointer-downs/ups stop at the first consumer in registration
/// order (earlier = higher priority); moves and resizes broadcast to all.</summary>
public sealed class CompositeUiInput(params IUiInput[] inputs) : IUiInput
{
    public bool Handle(in UiEvent uiEvent)
    {
        if (uiEvent.Kind is UiEventKind.PointerDown or UiEventKind.PointerUp)
        {
            foreach (var input in inputs)
            {
                if (input.Handle(in uiEvent)) return true;
            }
            return false;
        }
        var consumed = false;
        foreach (var input in inputs)
        {
            consumed |= input.Handle(in uiEvent);
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
