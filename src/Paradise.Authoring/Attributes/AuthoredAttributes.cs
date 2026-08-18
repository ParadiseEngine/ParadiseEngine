using System;

namespace Paradise.Authoring;

/// <summary>
/// Marks a plain record as AUTHORED data: something a human edits in an editor, which travels into
/// the scene export and comes back out as this same type at runtime.
///
/// One definition, many editors. A Roslyn generator turns every marked type in an assembly into an
/// engine-neutral SCHEMA document, and the editors read that document to build their own UI — the
/// Godot addon with one data-driven node, the Blender addon with <c>bpy.props</c>, the browser
/// editor with a form. None of them needs generated code, and adding a component is a record plus a
/// re-dump rather than editor work in three places.
///
/// This does NOT mean untyped. The record stays the runtime type: the game (or the engine)
/// deserializes the exported payload straight back into it through its own source-generated
/// <c>JsonSerializerContext</c>. The schema is an ADDITIONAL publication of the same declaration,
/// for hosts that cannot link against the type. Both, not either.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class AuthoredAttribute(string componentId) : Attribute
{
    /// <summary>The id this object travels under in the scene export, e.g. <c>paradise.rigidbody</c>.
    /// A contract shared by the editor that writes it and the runtime that reads it — renaming it
    /// orphans every exported document.</summary>
    public string ComponentId { get; } = componentId;

    /// <summary>Human-facing name for editors. Defaults to the type name.</summary>
    public string? DisplayName { get; set; }
}

// ---------------------------------------------------------------------------------------------
// Semantic hints. Deliberately NOT editor hints.
//
// A definition may never say `PropertyHint.Range` or `subtype='DISTANCE'` — the moment it names
// Godot's vocabulary, Blender and the web editor inherit Godot's vocabulary forever. It says what
// the number MEANS, and each editor maps that to its own widget: metres become a distance spinner
// in Blender, a plain float with a suffix in Godot, a number input with a unit label on the web.
// ---------------------------------------------------------------------------------------------

/// <summary>A length in metres.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MetersAttribute : Attribute;

/// <summary>An angle in radians.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RadiansAttribute : Attribute;

/// <summary>A duration in seconds.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecondsAttribute : Attribute;

/// <summary>A mass in kilograms.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class KilogramsAttribute : Attribute;

/// <summary>A probability or normalized fraction in 0..1.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class Unit01Attribute : Attribute;

/// <summary>An inclusive numeric range the editor should CLAMP OR WARN on.
///
/// Advisory, never authoritative: the runtime validator still decides what is playable, because
/// three editors that each enforce their own idea of "legal" will disagree, and only the game can
/// cross-check a value against the rest of its configuration (a pond against the penguin's body
/// radius, say). This exists so an editor can show the problem before export, not so it can be the
/// gate.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuthorRangeAttribute(double minimum, double maximum) : Attribute
{
    public double Minimum { get; } = minimum;
    public double Maximum { get; } = maximum;
}

/// <summary>One line of help, shown as a tooltip wherever the editor has one.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuthorDocAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}

/// <summary>
/// Draw this component as a wireframe BOX in the editor, sized from three of its own fields.
///
/// The point is that no editor needs a class to do it: "show me the volume I am authoring" is a
/// reusable primitive, not knowledge about any particular component. Declaring it here keeps the
/// record as the single source of everything — the fields, their units, their defaults, and now
/// how the thing looks while you edit it.
///
/// The box spans ±<paramref name="halfExtentXField"/> by ±<paramref name="halfExtentZField"/>,
/// from Y = 0 down to −<paramref name="depthField"/>. Y = 0 is the top on purpose: it is the
/// surface an authored volume is normally measured from.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class AuthorBoxGizmoAttribute(
    string halfExtentXField, string halfExtentZField, string depthField) : Attribute
{
    public string HalfExtentXField { get; } = halfExtentXField;
    public string HalfExtentZField { get; } = halfExtentZField;
    public string DepthField { get; } = depthField;
}

/// <summary>
/// This value is authored by REFERENCING one of the host's own objects, not by typing its numbers.
///
/// The editor shows a picker — Godot a typed node slot, Blender an object slot — and you edit the
/// referenced object with the host's own gizmo and handles. Nothing is mirrored and nothing syncs:
/// the reference IS the authoring surface.
///
/// The asymmetry to keep in mind: authored as a REFERENCE, exported as a VALUE. A host's node path
/// means nothing to the runtime, so the exporter bakes whatever is referenced into this record's
/// own fields.
///
/// Usable on a TYPE (the whole record is authored this way) or on a PROPERTY (just that field is).
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property,
    Inherited = false)]
public sealed class AuthoredByHostAttribute(string kind) : Attribute
{
    /// <summary>One of <c>AuthoredBySources</c>: shape, mesh, sprite, asset.</summary>
    public string Kind { get; } = kind;
}

/// <summary>
/// This record is authored by referencing a collision shape. Shorthand for
/// <c>[AuthoredByHost("shape")]</c>, and the original spelling of it.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class AuthorNativeShapeAttribute : Attribute;

/// <summary>
/// The file extensions an <c>asset</c> reference accepts, e.g. <c>[AuthorAssetKinds(".glb",
/// ".gltf")]</c>.
///
/// What the file IS, never a host's filter syntax — Godot wants <c>"*.glb,*.gltf"</c> and Blender
/// wants something else again, and each can build its own from this.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuthorAssetKindsAttribute(params string[] extensions) : Attribute
{
    public string[] Extensions { get; } = extensions;
}

/// <summary>
/// Show this property only while a SIBLING property holds a given value.
///
/// Declared rather than coded because the alternative is what it replaces: a seventeen-field
/// particle block permanently visible on every prop, or a host-specific visibility hook that every
/// other editor has to reimplement in its own language.
///
/// <paramref name="value"/> is compared at the sibling's own type — pass <c>true</c> for a bool,
/// the member name for an enum.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuthorVisibleWhenAttribute(string field, object value) : Attribute
{
    public string Field { get; } = field;
    public object Value { get; } = value;
}
