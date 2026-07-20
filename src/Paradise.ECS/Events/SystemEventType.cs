using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>
/// Process-wide registry mapping each unmanaged event type to a dense integer id and a factory for
/// its type-erased buffer.
/// </summary>
/// <remarks>
/// STAGE 1: ids are assigned in first-touch order — stable within a process (so the runtime merge,
/// snapshot, and their tests are deterministic) but NOT across processes. A later stage replaces
/// this with generator-assigned stable ids so on-disk save compatibility no longer depends on
/// touch order (see <c>docs/system-events.md</c> §9).
/// </remarks>
internal static class SystemEventTypeRegistry
{
    private static readonly object s_gate = new();
    private static readonly List<Func<ISystemEvents>> s_factories = new();

    /// <summary>Registers an event type's buffer factory and returns its assigned id.</summary>
    public static int Register(Func<ISystemEvents> factory)
    {
        lock (s_gate)
        {
            s_factories.Add(factory);
            return s_factories.Count - 1;
        }
    }

    /// <summary>Creates a fresh type-erased buffer for the given event-type id.</summary>
    public static ISystemEvents Create(int id)
    {
        lock (s_gate)
        {
            return s_factories[id]();
        }
    }
}

/// <summary>Per-type cached event id + size; first access registers the type with the registry.</summary>
/// <typeparam name="T">The unmanaged event type.</typeparam>
internal static class SystemEventType<T> where T : unmanaged
{
    /// <summary>Marshalled size of one event of this type.</summary>
    public static readonly int Size = Unsafe.SizeOf<T>();

    /// <summary>Dense process-wide id for this event type.</summary>
    public static readonly int Id = SystemEventTypeRegistry.Register(static () => new SystemEvents<T>());
}
