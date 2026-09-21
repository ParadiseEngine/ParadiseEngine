using System.Runtime.ExceptionServices;

namespace Paradise.Rendering.Pbr;

/// <summary>Collects teardown failures so independent owned resources still receive their release attempt.</summary>
internal struct ResourceCleanup
{
    private List<Exception>? _failures;

    public void Release<T>(T resource, Action<T> release)
    {
        try { release(resource); }
        catch (Exception error) { (_failures ??= []).Add(error); }
    }

    public void Dispose(IDisposable resource)
    {
        try { resource.Dispose(); }
        catch (Exception error) { (_failures ??= []).Add(error); }
    }

    /// <summary>Reports collected failures without masking an optional operation that triggered rollback.</summary>
    public readonly void ThrowIfFailed(Exception? original = null)
    {
        if (_failures is not { Count: > 0 } failures) return;
        if (original is not null)
            throw new AggregateException("Render operation and resource cleanup failed.", [original, .. failures]);
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        throw new AggregateException("Render resource cleanup failed.", failures);
    }
}
