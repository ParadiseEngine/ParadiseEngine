namespace Paradise.BT;

/// <summary>
/// Callers of this member must name their value arguments (PBT0013) when passing more than one.
/// Stamped on generated builder constructors and factories, whose parameters mirror a node's
/// surface: positional arguments there transpose silently when the surface changes.
/// </summary>
[AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method)]
public sealed class RequireNamedArgumentsAttribute : Attribute;
