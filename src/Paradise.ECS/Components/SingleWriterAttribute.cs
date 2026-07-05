namespace Paradise.ECS;

/// <summary>
/// Declares single-writer components: across a compilation, only one
/// <see cref="IEntitySystem"/>/<see cref="IChunkSystem"/> may take write access to such a
/// component (a non-readonly <c>ref T</c> field, or a <c>Span&lt;T&gt;</c> field). Read access
/// (<c>ref readonly T</c> / <c>ReadOnlySpan&lt;T&gt;</c>) is unrestricted.
///
/// On a struct: that component is single-writer. On an assembly
/// (<c>[assembly: SingleWriter]</c>): EVERY <c>[Component]</c> declared in that assembly is
/// single-writer — the natural default for a game assembly where each component has one owner
/// system.
/// </summary>
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
