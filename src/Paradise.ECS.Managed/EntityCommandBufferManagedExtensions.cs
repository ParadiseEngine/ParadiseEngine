using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Paradise.ECS;

public readonly struct AddManagedOp : ICommandExtension;
public readonly struct SetManagedOp : ICommandExtension;
public readonly struct RemoveManagedOp : ICommandExtension;

/// <summary>Records managed component changes with references owned by the command buffer.</summary>
public static class EntityCommandBufferManagedExtensions
{
    public static void AddManaged<T>(this EntityCommandBuffer commands, Entity entity, T? value)
        where T : class, IManagedComponent
        => Record<T, AddManagedOp>(commands, entity, value);

    public static void SetManaged<T>(this EntityCommandBuffer commands, Entity entity, T? value)
        where T : class, IManagedComponent
        => Record<T, SetManagedOp>(commands, entity, value);

    public static void RemoveManaged<T>(this EntityCommandBuffer commands, Entity entity)
        where T : class, IManagedComponent
        => Record<T, RemoveManagedOp>(commands, entity, null);

    private static void Record<T, TOp>(EntityCommandBuffer commands, Entity entity, T? value)
        where T : class, IManagedComponent
        where TOp : ICommandExtension
    {
        ArgumentNullException.ThrowIfNull(commands);
        var state = commands.GetOrCreateExtensionState<ManagedCommandState>();
        int index = state.Add(T.SlotTypeId, typeof(T), value);
        int slotId = T.SlotTypeId.Value;
        Span<byte> data = stackalloc byte[sizeof(int) * 2];
        MemoryMarshal.Write(data, in slotId);
        MemoryMarshal.Write(data[sizeof(int)..], in index);
        commands.RecordExtension<TOp>(entity, data);
    }
}

internal sealed class ManagedCommandState : ICommandBufferExtensionState
{
    private readonly List<(ComponentId SlotId, Type Type, object? Value)> _values = [];

    public int Add(ComponentId slotId, Type type, object? value)
    {
        int index = _values.Count;
        _values.Add((slotId, type, value));
        return index;
    }

    public object? Get(int index, ComponentId slotId, ImmutableArray<ManagedTypeInfo> types)
    {
        if ((uint)index >= (uint)_values.Count)
            throw new InvalidOperationException("The managed command staging index is invalid.");
        var staged = _values[index];
        if (staged.SlotId != slotId)
            throw new InvalidOperationException("The managed command staging slot does not match its payload.");
        foreach (var info in types)
        {
            if (info.SlotTypeId == slotId && info.Type == staged.Type)
                return staged.Value;
        }
        throw new InvalidOperationException("The managed command type does not match the target world's registry.");
    }

    public void Clear() => _values.Clear();
}
