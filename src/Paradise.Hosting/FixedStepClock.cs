namespace Paradise.Hosting;

/// <summary>Accumulates fixed ticks while discarding long stalls beyond the catch-up budget.</summary>
internal sealed class FixedStepClock(TimeSpan fixedStep, TimeSpan maxCatchUp)
{
    private long _remainder;

    internal int Advance(TimeSpan delta)
    {
        _remainder += Math.Clamp(delta.Ticks, 0, maxCatchUp.Ticks);
        var ticks = (int)(_remainder / fixedStep.Ticks);
        _remainder %= fixedStep.Ticks;
        return ticks;
    }
}
