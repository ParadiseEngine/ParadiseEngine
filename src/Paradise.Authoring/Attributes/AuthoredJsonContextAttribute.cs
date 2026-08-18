using System;

namespace Paradise.Authoring;

/// <summary>
/// Names the source-generated <c>JsonSerializerContext</c> this assembly's <c>[Authored]</c>
/// records are serializable through, which is what lets a registry be generated for them.
///
/// Declared once, at assembly level:
/// <code>
/// [assembly: AuthoredJsonContext(typeof(GameConfigJsonContext))]
/// </code>
///
/// It has to be pointed at rather than discovered: reflection is not an option here (it would pin
/// Godot's collectible AssemblyLoadContext and break C# hot-reload — godotengine/godot#78513, the
/// documented reason this whole contract is source-generated), and the schema generator cannot
/// OBSERVE what System.Text.Json's generator emits. It can, however, emit code that REFERENCES it:
/// generated sources are all compiled together, so <c>YourContext.Default.YourRecord</c> resolves
/// even though neither generator saw the other run. Verified before this was built.
///
/// Absent, no registry is generated and nothing else changes — an assembly that only publishes a
/// schema for editors needs none.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AuthoredJsonContextAttribute(Type contextType) : Attribute
{
    /// <summary>A <c>JsonSerializerContext</c> with a <c>[JsonSerializable]</c> entry for every
    /// <c>[Authored]</c> record in this assembly.</summary>
    public Type ContextType { get; } = contextType;
}
