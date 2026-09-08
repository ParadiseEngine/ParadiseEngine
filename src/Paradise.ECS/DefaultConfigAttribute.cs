namespace Paradise.ECS;

/// <summary>Marks the <see cref="IConfig"/> implementation used by generated world and storage aliases.</summary>
/// <remarks>The generator chooses the component-mask capacity from the registered component IDs.</remarks>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, AllowMultiple = false)]
public sealed class DefaultConfigAttribute : Attribute { }
