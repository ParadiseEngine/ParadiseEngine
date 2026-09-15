namespace Paradise.ECS;

/// <summary>Reports compiler errors for managed component access in this assembly's ECS systems.</summary>
/// <remarks>
/// Applies to entity, chunk, and world systems, including helpers declared inside them and managed query claims.
/// Ordinary application code may still create and access managed components.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class ForbidManagedComponentsInSystemsAttribute : Attribute;
