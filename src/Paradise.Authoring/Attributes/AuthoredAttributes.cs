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
/// <remarks>
/// <para>
/// The component's IDENTITY is not declared here. It comes from the BCL's own
/// <see cref="System.Runtime.InteropServices.GuidAttribute"/> on the same type:
/// </para>
/// <code>
/// [Guid("b7ab4dd8-c8da-4dc2-9e5e-192fd74deb11")]
/// [Authored(DisplayName = "Rigidbody")]
/// public sealed record RigidbodyComponentData { ... }
/// </code>
/// <para>
/// Generate that value with <c>uuidgen</c> or <c>Guid.NewGuid()</c>. Do not hand-type one, and do
/// not derive it from the id of the component next to it — a readable pattern invites the next
/// person to continue it, and two components that continue the same pattern collide.
/// </para>
/// <para>
/// A GUID rather than a name, because a name is two things at once and they have different
/// lifetimes: <c>paradise.rigidbody</c> was both "which component is this" and "what do we call
/// it", so renaming the component to something clearer orphaned every document that had already
/// authored it. The GUID is only the first of those, and is free to never change while the
/// <see cref="DisplayName"/> above it does.
/// </para>
/// <para>
/// <c>[Guid]</c> rather than an id parameter of our own, because .NET already has exactly this
/// attribute — "the stable GUID of this type" — and every tool that generates one already knows
/// where to put it. A second spelling would mean a type could carry two different GUIDs and be
/// right about neither.
/// </para>
/// <para>
/// A missing or malformed <c>[Guid]</c> is PAUT005, reported at compile time.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class AuthoredAttribute : Attribute
{
    /// <summary>Human-facing name for editors. Defaults to the type name. Safe to change: it is
    /// not what anything is looked up by.</summary>
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
/// This value is authored BY THE HOST — referenced through the host's own picker for a marker
/// kind, or supplied from the host object's own state for a value kind.
///
/// For a marker kind (<see cref="HostTransform"/>) the editor shows a picker and the exporter
/// fills this record's own leaves by name. For a composed kind (<see cref="HostShape"/>,
/// <see cref="HostLight"/>, <see cref="HostCamera"/>) the same picker fills the kind's own fields, nested when a property
/// is typed as the kind, or by name when the kind sits on a type. For a value kind
/// (<see cref="HostId"/>, <see cref="HostParent"/>, <see cref="HostMesh"/>, …) the host writes
/// one concrete value — a GUID, a name, a local TRS leaf — whose type must match the kind's,
/// checked by PAUT010.
///
/// A TYPE PARAMETER, not a string, so a kind that does not exist cannot compile and a kind that
/// carries a value declares what type it carries. Usable on a TYPE (marker kinds only — the whole
/// record is authored this way) or on a PROPERTY. A property may equivalently be TYPED as a value
/// kind itself, with no attribute; when both appear, the attribute wins (PAUT012).
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property,
    Inherited = false)]
public sealed class AuthoredByHostAttribute<THost> : Attribute where THost : struct, IHostKind;

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
