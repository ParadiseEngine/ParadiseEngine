using Paradise.Editor.Core.Document;

namespace Paradise.Editor.Core.Host;

/// <summary>An editor-owned kind of host object a schema field can be authored by pointing at:
/// a shape, a mesh, a light, a camera, a sprite.</summary>
/// <remarks>The editor is a host in the <c>AuthoredByHost</c> sense, exactly like the Godot and
/// Blender addons. Its host objects are ordinary components in the scene file under editor-minted
/// ids; the engine never learns them, and other hosts never need to. <see cref="AuthoredBy"/> is
/// the schema's kind name this component answers to, which is the whole binding: a field
/// declared with that kind renders as a picker over objects owning this component.</remarks>
public sealed record HostKind(Guid ComponentId, string AuthoredBy, string DisplayName)
{
    public bool Owns(SceneComponent component) => component.Id == ComponentId;

    public bool Owns(SceneObject entity) => entity.Components.Any(Owns);
}
