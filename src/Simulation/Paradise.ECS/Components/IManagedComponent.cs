namespace Paradise.ECS;

/// <summary>Identifies the generated unmanaged slot for a managed component.</summary>
/// <remarks>
/// Declared in core so query APIs can name managed components; the <c>Paradise.ECS.Managed</c>
/// package stores their objects and its generator implements this interface.
/// </remarks>
public interface IManagedComponent
{
    static abstract ComponentId SlotTypeId { get; }
    static abstract Guid Guid { get; }
}
