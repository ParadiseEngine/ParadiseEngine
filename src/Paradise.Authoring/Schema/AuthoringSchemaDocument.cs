using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paradise.Authoring;

/// <summary>
/// The engine-neutral description of every <see cref="AuthoredAttribute"/> type in an assembly:
/// what an editor needs in order to build a UI for data it cannot link against.
///
/// A C# editor host gets this typed, through <see cref="AuthoringSchemaReader"/>. Hosts that are
/// not C# — the Blender addon, the browser editor — parse the same document themselves, which is
/// the entire reason it exists rather than the generator emitting per-editor code.
/// </summary>
public sealed record AuthoringSchemaDocument
{
    /// <summary>Bumped when the SHAPE of this document changes in a way an existing reader would
    /// misparse. Adding an optional member does not qualify.
    ///
    /// v2 added the types the engine's own components need — arrays, vectors, quaternions, colour —
    /// plus conditional visibility and host-object references beyond collision shapes. A v1 reader
    /// would meet a <c>type</c> it has no control for, so the version had to move.
    ///
    /// v3 made <c>id</c> a GUID and added <c>type</c>. This one is not merely additive in the other
    /// direction either: every v1 and v2 document keys its components by a NAME, and there is no
    /// way to derive a component's GUID from <c>paradise.rigidbody</c>, so such a document cannot be
    /// upgraded on the way in — only regenerated.</summary>
    public const int CurrentVersion = 3;

    /// <summary>The oldest document this build still understands.
    ///
    /// Equal to <see cref="CurrentVersion"/> since v3, and that is the point rather than an
    /// oversight: a v2 document's string ids would each have to become a GUID this build has no
    /// mapping for. Rejecting it names the problem; accepting it would silently produce components
    /// with empty ids that resolve to nothing.</summary>
    public const int MinimumSupportedVersion = 3;

    public int Version { get; set; } = CurrentVersion;
    public List<AuthoredComponentSchema> Components { get; set; } = [];
}

/// <summary>One authored component: the id it travels under, and the fields a human edits.</summary>
public sealed record AuthoredComponentSchema
{
    /// <summary>Stable id, from the record's
    /// <see cref="System.Runtime.InteropServices.GuidAttribute"/>. What the exported payload is
    /// keyed by, and the only member here an editor may match on.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Fully qualified CLR name of the record, e.g. <c>Pingu.Core.Authoring.PoolConfig</c>.
    ///
    /// The FALLBACK key, and the thing that makes a GUID id survivable in a text document: it is
    /// what lets a human reading the schema, a diff, or a broken payload tell which component a
    /// bare GUID refers to. Editors show it and may resolve by it when the id misses; nothing
    /// should prefer it, because a type rename moves it and the GUID exists precisely so identity
    /// does not move.
    /// </summary>
    public string Type { get; set; } = "";

    /// <summary>Human-facing name. Falls back to the type name when nothing was declared.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>How to draw the component while it is being edited, or null for no gizmo.</summary>
    public AuthoredGizmoSchema? Gizmo { get; set; }

    /// <summary>Set when the WHOLE component is authored by pointing at one of the host's own
    /// objects — see <see cref="AuthoredBySources"/>. Its fields are then what gets baked out of
    /// that object, and an editor shows one picker instead of a form.</summary>
    public string? AuthoredBy { get; set; }

    public List<AuthoredFieldSchema> Fields { get; set; } = [];
}

/// <summary>
/// One editable value. Recursive: a composed field carries its own <see cref="Fields"/>, so an
/// editor renders it as a nested group without knowing anything about the type inside it.
/// </summary>
public sealed record AuthoredFieldSchema
{
    public string Name { get; set; } = "";

    /// <summary>One of <see cref="AuthoredFieldTypes"/>.</summary>
    public string Type { get; set; } = "";

    /// <summary>One of <see cref="AuthoredUnits"/>, or null. SEMANTIC — what the number means,
    /// never which widget to use.</summary>
    public string? Unit { get; set; }

    /// <summary>One line of help for a tooltip.</summary>
    public string? Doc { get; set; }

    /// <summary>Advisory bounds. The runtime validator, not the editor, decides what is playable.</summary>
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }

    /// <summary>The declared default, as a JSON value at its own type — a number for a number, a
    /// bool for a bool. Kept as <see cref="JsonElement"/> because the schema describes fields of
    /// several types and this one member has to carry all of them.</summary>
    public JsonElement? Default { get; set; }

    /// <summary>The legal names when <see cref="Type"/> is <see cref="AuthoredFieldTypes.Enum"/>.</summary>
    public List<string>? Values { get; set; }

    /// <summary>Non-null when the value is authored through something other than a typed control —
    /// see <see cref="AuthoredBySources"/>. The <see cref="Fields"/> below then describe what gets
    /// BAKED out of that reference at export time.</summary>
    public string? AuthoredBy { get; set; }

    /// <summary>Set when this field is a composed object. An entity's authored data is a tree,
    /// not a row.</summary>
    public List<AuthoredFieldSchema>? Fields { get; set; }

    /// <summary>The element schema when <see cref="Type"/> is <see cref="AuthoredFieldTypes.Array"/>.
    /// An editor renders one row per element and lets the author add and remove them.</summary>
    public AuthoredFieldSchema? Items { get; set; }

    /// <summary>Show this field only while another field of the same component holds a given value,
    /// or always when null.
    ///
    /// Data rather than behaviour on purpose: a seventeen-field particle block permanently visible
    /// on every prop is the state this replaces, and each editor would otherwise reimplement the
    /// same conditionals in its own language.</summary>
    public AuthoredVisibilitySchema? VisibleWhen { get; set; }

    /// <summary>File extensions this field accepts, when it is authored by picking an asset. What
    /// the file IS — never a host's filter syntax.</summary>
    public List<string>? AssetKinds { get; set; }
}

/// <summary>A field is shown only while <see cref="Field"/> equals <c>Equals</c>.</summary>
public sealed record AuthoredVisibilitySchema
{
    /// <summary>Sibling field name within the same component.</summary>
    public string Field { get; set; } = "";

    /// <summary>The value that reveals the guarded field, as a JSON value at the sibling's own
    /// type — <c>true</c> for a bool, a member name for an enum.
    ///
    /// Named <c>EqualTo</c> in C# because a record cannot declare a property called Equals, but the
    /// document key stays <c>equals</c> so the schema reads naturally in every other language.</summary>
    [JsonPropertyName("equals")]
    public JsonElement EqualTo { get; set; }
}

/// <summary>A shape to draw in the viewport, sized from the component's own fields.</summary>
public sealed record AuthoredGizmoSchema
{
    /// <summary>Currently only <c>box</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Names of the fields supplying the box's dimensions. The box spans
    /// ±<see cref="HalfExtentX"/> by ±<see cref="HalfExtentZ"/>, from Y = 0 down to
    /// −<see cref="Depth"/>.</summary>
    public string? HalfExtentX { get; set; }
    public string? HalfExtentZ { get; set; }
    public string? Depth { get; set; }
}

/// <summary>The closed set of <see cref="AuthoredFieldSchema.Type"/> values. Deliberately few:
/// every one has an obvious control in Godot, Blender and HTML alike, and each new one is work in
/// every editor.</summary>
public static class AuthoredFieldTypes
{
    public const string Float = "float";
    public const string Int = "int";
    public const string Bool = "bool";
    public const string String = "string";
    public const string Enum = "enum";

    /// <summary>A composed value; read <see cref="AuthoredFieldSchema.Fields"/>.</summary>
    public const string Object = "object";

    /// <summary>A repeated value; read <see cref="AuthoredFieldSchema.Items"/>.</summary>
    public const string Array = "array";

    // Small fixed-size numeric aggregates. Leaves, NOT composed objects: every editor has a
    // dedicated control for them, and decomposing a Vector3 into three floats would throw that
    // away and make the schema noisier at the same time.
    public const string Vector2 = "vector2";
    public const string Vector3 = "vector3";
    public const string Quaternion = "quaternion";
    public const string Color = "color";

    /// <summary>
    /// A 4×4 transform, sixteen floats, COLUMN-MAJOR — the contract's own convention, with the
    /// translation at flat indices 12/13/14.
    ///
    /// <b>An editor should not draw sixteen boxes for it.</b> It is here because it is a leaf like
    /// the vectors above rather than a composed object, but unlike them it is not a value anybody
    /// types: a placement is something you move with the host's own gizmo. A field of this type is
    /// almost always DERIVED — an exporter writes it for the object it is exporting — so the
    /// reasonable editor treatments are to show it read-only or to omit it, and a raw float grid is
    /// the one treatment that is actively wrong.
    /// </summary>
    public const string Matrix4x4 = "matrix4x4";
}

/// <summary>The closed set of <see cref="AuthoredFieldSchema.Unit"/> values.</summary>
public static class AuthoredUnits
{
    public const string Meters = "meters";
    public const string Radians = "radians";
    public const string Seconds = "seconds";
    public const string Kilograms = "kilograms";
    public const string Unit01 = "unit01";
}

/// <summary>
/// The closed set of <see cref="AuthoredFieldSchema.AuthoredBy"/> values: KINDS OF HOST OBJECT a
/// value can be authored by pointing at, rather than by typing its numbers.
///
/// The asymmetry these all share is authored as a REFERENCE, exported as a VALUE — the editor bakes
/// whatever it points at into the field's own numbers, because a host's node path means nothing to
/// the runtime. Each editor maps a kind to its own picker: Godot a typed node slot, Blender an
/// object slot. The kinds name what the object IS, never what any one editor calls it.
/// </summary>
public static class AuthoredBySources
{
    /// <summary>A collision shape, edited with the host's own handles.</summary>
    public const string Shape = "shape";

    /// <summary>A renderable mesh, whose source asset is resolved at export.</summary>
    public const string Mesh = "mesh";

    /// <summary>A 2D billboard sprite, whose sheet and quad geometry are read at export.</summary>
    public const string Sprite = "sprite";

    /// <summary>A light, whose colour, energy, shadows and aim are read at export. Its DIRECTION
    /// comes from the referenced object's orientation, which is why you aim a light by rotating it
    /// rather than by typing a vector.</summary>
    public const string Light = "light";

    /// <summary>A camera, whose lens and aim are read at export. Where it stands and which way it
    /// looks come from the referenced object's pose, which is why you frame a shot by moving the
    /// camera rather than by typing a vector.</summary>
    public const string Camera = "camera";

    /// <summary>A spritesheet animation's geometry — which sheet, how it divides, how big a frame
    /// is — read off the host's own sprite object (<see cref="HostSpriteSheet"/>). Distinct from
    /// <see cref="Sprite"/>, which is the sheet reference alone.</summary>
    public const string SpriteSheet = "sprite-sheet";

    /// <summary>The scene's lighting mood — ambient, background, fog, tone mapping
    /// (<see cref="HostEnvironment"/>).</summary>
    public const string Environment = "environment";

    /// <summary>A file on disk, baked as the asset's GUID; see
    /// <see cref="AuthoredFieldSchema.AssetKinds"/>.</summary>
    public const string Asset = "asset";

    /// <summary>
    /// ANOTHER OBJECT IN THE SCENE: point at it, and the exporter bakes its durable GUID into this
    /// field — the same identity its <c>meta</c> carries.
    ///
    /// The odd one of the set, because what it bakes is not a value read off the referenced object
    /// — a pose, a shape, a colour — but the reference itself. A runtime resolves it against the
    /// scene once the whole walk is done, since the target may be authored after the thing
    /// pointing at it.
    ///
    /// <b>A GUID, not a name or an index.</b> Names are not unique. An index would break the
    /// moment an exporter reordered or dropped an object. The GUID is the identity the host
    /// already minted, so a broken reference names the same thing <c>meta</c> does.
    ///
    /// The wire type stays <c>string</c> and the schema version stays put, but the baked text is
    /// the GUID: a host that still writes the display name fails the generated reader's
    /// <c>Guid.Parse</c>. There is deliberately no string hatch here, unlike mesh/sprite/asset
    /// (PAUT010's baked-path allowance), because a name was never an identity. Hosts move with the
    /// package bump that ships this.
    /// </summary>
    public const string Entity = "entity";

    /// <summary>The host object's parent in the scene tree (<see cref="HostParent"/>).</summary>
    public const string Parent = "parent";

    /// <summary>
    /// An object whose WORLD POSE is the value: point at an empty (or anything else placeable) and
    /// the exporter bakes where it stands into this record's own fields.
    ///
    /// What you are aiming is the host's own move/rotate gizmo, which is the entire reason this is
    /// a reference rather than three floats — a destination you can SEE is a destination you can
    /// place correctly, and a vector typed into a panel is one nobody can check without running the
    /// game.
    ///
    /// Baked BY FIELD NAME, so the record decides how much of the pose it wants: an exporter fills
    /// whichever of <c>Position</c> (vector3), <c>Rotation</c> (quaternion), <c>Yaw</c> (float) and
    /// <c>Scale</c> (vector3) are declared, and ignores the rest. That keeps the host general — it
    /// never learns what any particular record MEANS by a pose.
    /// </summary>
    public const string Transform = "transform";

    /// <summary>The host object's own durable identity (<see cref="HostId"/>).</summary>
    public const string Id = "id";

    /// <summary>The host object's display name (<see cref="HostName"/>).</summary>
    public const string Name = "name";

    /// <summary>The host object's local translation (<see cref="HostLocalPosition"/>).</summary>
    public const string LocalPosition = "local-position";

    /// <summary>The host object's local rotation, canonical quaternion (<see cref="HostLocalRotation"/>).</summary>
    public const string LocalRotation = "local-rotation";

    /// <summary>The host object's local scale (<see cref="HostLocalScale"/>).</summary>
    public const string LocalScale = "local-scale";
}

/// <summary>
/// Source-generated metadata for the schema document. Source-generated for the same reason the
/// export contract is: a reflection serializer pins Godot's collectible AssemblyLoadContext and
/// breaks C# hot-reload (godotengine/godot#78513), and this type is parsed inside the Godot editor.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AuthoringSchemaDocument))]
public sealed partial class AuthoringSchemaJsonContext : JsonSerializerContext;
