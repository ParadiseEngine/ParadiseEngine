using Paradise.Windowing;
namespace Paradise.Ui.Test;

/// <summary>CompositeUiInput fan-out semantics: pointer downs/ups stop at the first consumer
/// in registration order; everything else broadcasts to all inputs and ORs the consumed
/// flags; Tick reaches every input.</summary>
public class CompositeUiInputTests
{
    private sealed class RecordingInput(bool consumes) : IUiInput
    {
        public List<WindowEventKind> Seen { get; } = [];
        public int Ticks { get; private set; }

        public bool Handle(in WindowEvent uiEvent)
        {
            Seen.Add(uiEvent.Kind);
            return consumes;
        }

        public void Tick(double simTimeSeconds) => Ticks++;
    }

    [Test]
    public async Task pointer_down_stops_at_the_first_consumer()
    {
        var first = new RecordingInput(consumes: true);
        var second = new RecordingInput(consumes: true);
        var composite = new CompositeUiInput(first, second);

        var consumed = composite.Handle(WindowEvent.Mouse(PointerButton.Left, pressed: true, 1f, 2f));

        await Assert.That(consumed).IsTrue();
        await Assert.That(first.Seen).Count().IsEqualTo(1);
        await Assert.That(second.Seen).IsEmpty();
    }

    [Test]
    public async Task unconsumed_pointer_down_falls_through_every_input()
    {
        var first = new RecordingInput(consumes: false);
        var second = new RecordingInput(consumes: false);
        var composite = new CompositeUiInput(first, second);

        var consumed = composite.Handle(WindowEvent.Mouse(PointerButton.Left, pressed: true, 1f, 2f));

        await Assert.That(consumed).IsFalse();
        await Assert.That(first.Seen).Count().IsEqualTo(1);
        await Assert.That(second.Seen).Count().IsEqualTo(1);
    }

    [Test]
    public async Task moves_broadcast_to_all_inputs_even_after_a_consumer()
    {
        var first = new RecordingInput(consumes: true);
        var second = new RecordingInput(consumes: false);
        var composite = new CompositeUiInput(first, second);

        var consumed = composite.Handle(WindowEvent.PointerMove(5f, 6f));

        await Assert.That(consumed).IsTrue();
        await Assert.That(first.Seen).Count().IsEqualTo(1);
        await Assert.That(second.Seen).Count().IsEqualTo(1);
    }

    [Test]
    public async Task resize_broadcasts_and_reports_unconsumed()
    {
        var first = new RecordingInput(consumes: false);
        var second = new RecordingInput(consumes: false);
        var composite = new CompositeUiInput(first, second);

        var consumed = composite.Handle(WindowEvent.Resize(640f, 480f));

        await Assert.That(consumed).IsFalse();
        await Assert.That(first.Seen).Count().IsEqualTo(1);
        await Assert.That(second.Seen).Count().IsEqualTo(1);
    }

    [Test]
    public async Task tick_reaches_every_input()
    {
        var first = new RecordingInput(consumes: false);
        var second = new RecordingInput(consumes: true);
        var composite = new CompositeUiInput(first, second);

        composite.Tick(1.5);
        composite.Tick(3.0);

        await Assert.That(first.Ticks).IsEqualTo(2);
        await Assert.That(second.Ticks).IsEqualTo(2);
    }
}
