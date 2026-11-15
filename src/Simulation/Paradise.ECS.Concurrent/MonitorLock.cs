using System.Runtime.CompilerServices;

namespace Paradise.ECS.Concurrent;

/// <summary>A mutual-exclusion lock with <see cref="System.Threading.Lock"/>'s scope shape, built on <see cref="Monitor"/>.</summary>
/// <remarks>Coyote 1.7.11 rewrites <see cref="Monitor"/> but not <c>System.Threading.Lock.EnterScope</c>, so
/// systematic tests could not interleave code that used the latter (docs/development/concurrency.md).</remarks>
internal sealed class MonitorLock
{
    private readonly object _gate = new();

    /// <summary>Enters the lock until the returned scope is disposed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Scope EnterScope()
    {
        Monitor.Enter(_gate);
        return new Scope(_gate);
    }

    /// <summary>Releases the lock when disposed.</summary>
    public readonly ref struct Scope
    {
        private readonly object _gate;

        internal Scope(object gate) => _gate = gate;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() => Monitor.Exit(_gate);
    }
}
