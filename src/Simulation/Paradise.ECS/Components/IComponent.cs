namespace Paradise.ECS;

/// <summary>
/// Interface for ECS component types.
/// Use <see cref="ComponentAttribute"/> on partial structs to implement this automatically.
/// </summary>
/// <remarks>
/// <para>
/// The generated module initializer assigns automatic IDs by descending alignment, then fully
/// qualified type name, skipping explicitly assigned IDs.
/// </para>
/// <para>
/// Automatic IDs depend on the registered types and their alignment; use GUIDs for durable identity.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Component]
/// public partial struct Position
/// {
///     public float X, Y, Z;
/// }
/// </code>
/// </example>
public interface IComponent
{
    /// <summary>The component type ID assigned by the generated module initializer.</summary>
    static abstract ComponentId TypeId { get; }

    /// <summary>The stable GUID for this component type, or <see cref="System.Guid.Empty"/> if not specified.</summary>
    /// <remarks>
    /// Unlike <see cref="TypeId"/>, whose automatic assignment depends on the component registry,
    /// this GUID provides stable identification across compilations when specified
    /// via <see cref="System.Runtime.InteropServices.GuidAttribute"/>.
    /// </remarks>
    static abstract Guid Guid { get; }

    /// <summary>The size of this component in bytes.</summary>
    static abstract int Size { get; }

    /// <summary>The alignment of this component in bytes.</summary>
    static abstract int Alignment { get; }
}
