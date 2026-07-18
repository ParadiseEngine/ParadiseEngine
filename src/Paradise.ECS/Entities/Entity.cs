using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>
/// A lightweight handle to an entity in the ECS world.
/// Entities are unique identifiers that tie components together.
/// </summary>
/// <param name="Id">The unique identifier of the entity.</param>
/// <param name="Version">Incrementing version for destroyed entity detection.</param>
public readonly record struct Entity(int Id, uint Version)
{
    /// <summary>
    /// The Invalid entity handle. Equal to <c>default(Entity)</c>.
    /// </summary>
    public static readonly Entity Invalid = default;

    /// <summary>
    /// Gets whether this entity handle is valid (not the Invalid entity or default).
    /// Valid entities have Version >= 1; Version 0 indicates an invalid entity.
    /// Note: Does not check if the entity is still alive in the manager.
    /// </summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Version > 0;
    }

    /// <summary>
    /// Gets whether this handle is a deferred-spawn placeholder returned by
    /// <see cref="EntityCommandBuffer.Spawn"/> (high bit of <see cref="Id"/> set; real entities
    /// always have non-negative IDs). A placeholder is valid ONLY as an argument to commands
    /// recorded on the SAME <see cref="EntityCommandBuffer"/>; it must not be stored into
    /// component data or passed to World methods. For a placeholder, <see cref="Id"/> encodes
    /// the buffer-local spawn index in the low 31 bits and <see cref="Version"/> carries the
    /// owning buffer's unique ID.
    /// </summary>
    public bool IsPlaceholder
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Id < 0;
    }

    public override string ToString() =>
        IsPlaceholder
            ? $"Entity(Placeholder #{Id & 0x7FFFFFFF}, Buffer: {Version})"
            : IsValid ? $"Entity(Id: {Id}, Ver: {Version})" : "Entity(Invalid)";
}
