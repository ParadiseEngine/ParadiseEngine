namespace Paradise.BT;

/// <summary>Requires named value arguments when a generated builder call supplies more than one.</summary>
[AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method)]
public sealed class RequireNamedArgumentsAttribute : Attribute;
