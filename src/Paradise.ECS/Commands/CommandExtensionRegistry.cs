namespace Paradise.ECS;

/// <summary>
/// Process-local dense ids for <see cref="ICommandExtension"/> types. Stable within a process
/// (first-touch order) but not across processes — the command buffer is not a serialized format.
/// </summary>
internal static class CommandExtensionRegistry
{
    private static readonly object s_gate = new();
    private static readonly List<Type> s_types = new();

    public static short Register(Type type)
    {
        lock (s_gate)
        {
            if (s_types.Count > short.MaxValue)
            {
                throw new InvalidOperationException("Too many command extension types.");
            }

            s_types.Add(type);
            return (short)(s_types.Count - 1);
        }
    }

    public static Type TypeOf(short id)
    {
        lock (s_gate)
        {
            if ((uint)id >= (uint)s_types.Count)
            {
                throw new InvalidOperationException($"Unknown command extension id {id}.");
            }

            return s_types[id];
        }
    }
}

/// <summary>Per-type cached extension id; first access registers the type.</summary>
internal static class CommandExtensionId<TOp> where TOp : ICommandExtension
{
    public static readonly short Value = CommandExtensionRegistry.Register(typeof(TOp));
}
