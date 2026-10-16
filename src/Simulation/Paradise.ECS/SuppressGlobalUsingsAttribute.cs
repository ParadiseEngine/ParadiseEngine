namespace Paradise.ECS;

/// <summary>
/// Suppresses component, queryable and system global aliases generated for this assembly.
/// Use this attribute when your project has multiple ECS libraries that define types
/// with the same names (e.g., World, Query, ComponentMask).
/// </summary>
/// <remarks>
/// When applied at assembly level, this suppresses aliases such as:
/// <code>
/// global using World = ...;
/// global using Query = ...;
/// global using SharedArchetypeMetadata = ...;
/// global using ArchetypeRegistry = ...;
/// global using ComponentMask = ...;
/// global using QueryBuilder = ...;
/// global using QueryableRegistry = ...;
/// </code>
/// Use fully qualified types or local aliases instead; TagGenerator emits its separate TagMask alias regardless of this attribute.
/// </remarks>
/// <example>
/// <code>
/// [assembly: SuppressGlobalUsings]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class SuppressGlobalUsingsAttribute : Attribute { }
