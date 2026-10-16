using Paradise.Windowing;
using TUnit.Assertions.Enums;

namespace Paradise.Ui.Test;

/// <summary>Checks ordered button routing, broadcast consumption and fixed-tick delivery.</summary>
public class CompositeUiInputTests
{
    private sealed class RecordingInput(bool consumes) : IUiInput
    {
        public List<WindowEvent> Seen { get; } = [];
        public List<double> TickTimes { get; } = [];

        public bool Handle(in WindowEvent uiEvent)
        {
            Seen.Add(uiEvent);
            return consumes;
        }

        public void Tick(double simTimeSeconds) => TickTimes.Add(simTimeSeconds);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task pointer_transition_stops_at_the_first_consumer(bool pressed)
    {
        var first = new RecordingInput(consumes: true);
        var second = new RecordingInput(consumes: true);
        var composite = new CompositeUiInput(first, second);

        var inputEvent = WindowEvent.Mouse(PointerButton.Left, pressed, 1f, 2f);
        var consumed = composite.Handle(inputEvent);

        await Assert.That(consumed).IsTrue();
        await Assert.That(first.Seen).IsEquivalentTo(new[] { inputEvent }, CollectionOrdering.Matching);
        await Assert.That(second.Seen).IsEmpty();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task unconsumed_pointer_transition_falls_through_every_input(bool pressed)
    {
        var first = new RecordingInput(consumes: false);
        var second = new RecordingInput(consumes: false);
        var composite = new CompositeUiInput(first, second);

        var inputEvent = WindowEvent.Mouse(PointerButton.Left, pressed, 1f, 2f);
        var consumed = composite.Handle(inputEvent);

        await Assert.That(consumed).IsFalse();
        await Assert.That(first.Seen).IsEquivalentTo(new[] { inputEvent }, CollectionOrdering.Matching);
        await Assert.That(second.Seen).IsEquivalentTo(new[] { inputEvent }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task pointer_transition_stops_at_a_later_consumer(bool pressed)
    {
        var first = new RecordingInput(consumes: false);
        var second = new RecordingInput(consumes: true);
        var third = new RecordingInput(consumes: true);
        var composite = new CompositeUiInput(first, second, third);
        var inputEvent = WindowEvent.Mouse(PointerButton.Left, pressed, 1f, 2f);

        var consumed = composite.Handle(inputEvent);

        await Assert.That(consumed).IsTrue();
        await Assert.That(first.Seen).IsEquivalentTo(new[] { inputEvent }, CollectionOrdering.Matching);
        await Assert.That(second.Seen).IsEquivalentTo(new[] { inputEvent }, CollectionOrdering.Matching);
        await Assert.That(third.Seen).IsEmpty();
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task moves_broadcast_and_combine_consumed_flags(bool firstConsumes, bool secondConsumes, bool expectedConsumed)
    {
        var first = new RecordingInput(firstConsumes);
        var second = new RecordingInput(secondConsumes);
        var composite = new CompositeUiInput(first, second);
        var inputEvent = WindowEvent.PointerMove(5f, 6f);

        var consumed = composite.Handle(inputEvent);

        await Assert.That(consumed).IsEqualTo(expectedConsumed);
        await Assert.That(first.Seen).IsEquivalentTo(new[] { inputEvent }, CollectionOrdering.Matching);
        await Assert.That(second.Seen).IsEquivalentTo(new[] { inputEvent }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task resize_broadcasts_and_reports_unconsumed()
    {
        var first = new RecordingInput(consumes: false);
        var second = new RecordingInput(consumes: false);
        var composite = new CompositeUiInput(first, second);
        var inputEvent = WindowEvent.Resize(640f, 480f);

        var consumed = composite.Handle(inputEvent);

        await Assert.That(consumed).IsFalse();
        await Assert.That(first.Seen).IsEquivalentTo(new[] { inputEvent }, CollectionOrdering.Matching);
        await Assert.That(second.Seen).IsEquivalentTo(new[] { inputEvent }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task tick_reaches_every_input()
    {
        var first = new RecordingInput(consumes: false);
        var second = new RecordingInput(consumes: true);
        var composite = new CompositeUiInput(first, second);

        composite.Tick(1.5);
        composite.Tick(3.0);

        await Assert.That(first.TickTimes).IsEquivalentTo(new[] { 1.5, 3.0 }, CollectionOrdering.Matching);
        await Assert.That(second.TickTimes).IsEquivalentTo(new[] { 1.5, 3.0 }, CollectionOrdering.Matching);
    }
}
