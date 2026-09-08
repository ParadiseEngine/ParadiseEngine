namespace Paradise.ECS;

/// <summary>Limits each marked component to one system writer per compilation.</summary>
/// <remarks>
/// Apply to a component or to an assembly's components. Mutable refs and spans count as writes;
/// read-only access is unrestricted.
/// </remarks>
/// <remarks>
/// Enforced at compile time by the <c>PECS3008</c> analyzer. Writes made from plain managed code
/// (e.g. <c>world.GetComponent&lt;T&gt;()</c>) are not — and cannot be — tracked by the
/// analyzer; the contract covers system field injection only.
/// </remarks>
/// <example>
/// <code>
/// // per component:
/// [Component]
/// [SingleWriter]
/// public partial struct MoveIntent
/// {
///     public System.Numerics.Vector3 DesiredVelocity;
/// }
///
/// // or assembly-wide:
/// [assembly: Paradise.ECS.SingleWriter]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class SingleWriterAttribute : Attribute
{
}
