using System.Runtime.InteropServices;

namespace Paradise.ECS;

/// <summary>Deferred tag add. Payload is the tag id as a little-endian <see cref="int"/>.</summary>
public readonly struct AddTagOp : ICommandExtension;

/// <summary>Deferred tag remove. Payload is the tag id as a little-endian <see cref="int"/>.</summary>
public readonly struct RemoveTagOp : ICommandExtension;

/// <summary>
/// Typed tag add/remove on an <see cref="EntityCommandBuffer"/> — the same spelling as
/// <c>AddComponent&lt;T&gt;</c> / <c>RemoveComponent&lt;T&gt;</c>, for a bit instead of a row.
/// </summary>
public static class EntityCommandBufferTagExtensions
{
    public static void AddTag<TTag>(this EntityCommandBuffer commands, Entity entity)
        where TTag : ITag
    {
        int tagId = TTag.TagId.Value;
        Span<byte> data = stackalloc byte[sizeof(int)];
        MemoryMarshal.Write(data, in tagId);
        commands.RecordExtension<AddTagOp>(entity, data);
    }

    public static void RemoveTag<TTag>(this EntityCommandBuffer commands, Entity entity)
        where TTag : ITag
    {
        int tagId = TTag.TagId.Value;
        Span<byte> data = stackalloc byte[sizeof(int)];
        MemoryMarshal.Write(data, in tagId);
        commands.RecordExtension<RemoveTagOp>(entity, data);
    }
}
