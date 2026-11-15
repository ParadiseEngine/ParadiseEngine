using Paradise.Windowing;

namespace Paradise.Hosting;

/// <summary>One simulation step: the fixed ticks owed since the last step, preceded by pending input.</summary>
/// <remarks>Shared by <see cref="ParadiseHost"/>'s simulation thread and <see cref="CooperativeHost"/>'s
/// frames, so both drivers deliver input, tick and catch up identically.</remarks>
internal sealed class SimulationStepper
{
    private readonly IHostSimulation _simulation;
    private readonly IWindow _window;
    private readonly HostClock _clock;
    private readonly FixedStepClock _steps;
    private TimeSpan _previous;

    internal SimulationStepper(IHostSimulation simulation, HostContext context, HostOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(simulation.FixedStep, TimeSpan.Zero);
        _simulation = simulation;
        _window = context.Window;
        _clock = context.Clock;
        _steps = new FixedStepClock(simulation.FixedStep, options.MaxCatchUp);
        _previous = _clock.Elapsed;
    }

    /// <summary>Runs the ticks owed by now; false when none was owed (nothing ran, input stays queued).</summary>
    /// <param name="stopRequested">Checked between ticks, so a stop does not wait for a long catch-up batch.</param>
    internal bool Step(Func<bool> stopRequested)
    {
        var now = _clock.Elapsed;
        var ticks = _steps.Advance(now - _previous);
        _previous = now;
        if (ticks == 0) return false;

        while (_window.TryReadEvent(out var input))
        {
            _simulation.HandleInput(in input);
        }

        for (var i = 0; i < ticks && !stopRequested(); i++)
        {
            _simulation.Tick();
        }

        _simulation.AfterTicks();
        return true;
    }
}
