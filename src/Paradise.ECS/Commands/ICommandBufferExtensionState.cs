namespace Paradise.ECS;

/// <summary>Owns package-specific staging data for a deferred command buffer.</summary>
/// <remarks>State is private to one buffer and reused across recordings; Clear must release staged references and must not throw.</remarks>
public interface ICommandBufferExtensionState
{
    /// <summary>Releases the current recording's staged data while retaining reusable storage.</summary>
    void Clear();
}
