namespace Paradise.Hosting;

/// <summary>Accumulates fixed ticks while discarding long stalls beyond the catch-up budget.</summary>
internal sealed class FixedStepClock
{
    private readonly ulong _fixedStep;
    private readonly long _maxCatchUp;
    private ulong _remainder;

    internal FixedStepClock(TimeSpan fixedStep, TimeSpan maxCatchUp)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fixedStep, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxCatchUp, TimeSpan.Zero);
        _fixedStep = (ulong)fixedStep.Ticks;
        _maxCatchUp = maxCatchUp.Ticks;
        // A previous frame may leave up to one fixed step minus one tick outstanding.
        var maximumAccumulated = (ulong)_maxCatchUp + _fixedStep - 1;
        if (maximumAccumulated / _fixedStep > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCatchUp), "The maximum catch-up batch must fit in an Int32.");
        }
    }

    internal int Advance(TimeSpan delta)
    {
        // Two nonnegative Int64 intervals fit in UInt64 even when their sum exceeds Int64.MaxValue.
        _remainder += (ulong)Math.Clamp(delta.Ticks, 0, _maxCatchUp);
        var ticks = (int)(_remainder / _fixedStep);
        _remainder %= _fixedStep;
        return ticks;
    }
}
