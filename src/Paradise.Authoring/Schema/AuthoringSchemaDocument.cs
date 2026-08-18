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
    /// misparse. Adding an optional member does not qualify.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<AuthoredComponentSchema> Components { get; set; } = [];
}

/// <summary>One authored component: the id it travels under, and the fields a human edits.</summary>
public sealed record AuthoredComponentSchema
{
    /// <summary>Stable id, e.g. <c>paradise.rigidbody</c>. What the exported payload is keyed by.</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-facing name. Falls back to the type name when nothing was declared.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>How to draw the component while it is being edited, or null for no gizmo.</summary>
    public AuthoredGizmoSchema? Gizmo { get; set; }

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

/// <summary>The closed set of <see cref="AuthoredFieldSchema.AuthoredBy"/> values.</summary>
public static class AuthoredBySources
{
    /// <summary>Authored by pointing at the host's own shape object and editing it with the host's
    /// own handles; baked to values at export.</summary>
    public const string NativeShape = "nativeShape";
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
