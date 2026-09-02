using System;
using System.Numerics;

namespace Paradise.Authoring;

/// <summary>
/// A KIND OF HOST OBJECT a value can be authored by — the typed spelling of
/// <see cref="AuthoredBySources"/>' strings.
/// </summary>
/// <remarks>
/// <para>
/// A type rather than a string, so the binding is CHECKABLE: <c>[AuthoredByHost&lt;THost&gt;]</c>
/// constrains its argument to these structs (a typo cannot compile), and a kind that carries a
/// value declares that value's type, letting the analyzer verify the authored field matches
/// (PAUT010). The string never checked anything.
/// </para>
/// <para>
/// Each kind still carries its <c>Kind</c> const — the string that reaches
/// <c>authoring-schema.json</c>'s <c>authoredBy</c>, because the schema is what hosts that cannot
/// link against these types (the pure-Python Blender addon) read.
/// </para>
/// <para>
/// Three families.
/// </para>
/// <para>
/// A MARKER kind (<see cref="HostTransform"/>) names a host object the whole record or field is
/// authored by pointing at; it carries no value of its own and may sit on a type or a property.
/// The exporter fills the record's own leaves by name.
/// </para>
/// <para>
/// A VALUE kind (<see cref="HostId"/>, <see cref="HostParent"/>, <see cref="HostAsset"/>, …) is
/// one concrete value the host supplies; it binds a single property — by attribute, or by typing
/// the property as the kind itself — and declares the type that property must have.
/// </para>
/// <para>
/// A COMPOSED kind (<see cref="HostShape"/>, <see cref="HostLight"/>, <see cref="HostCamera"/>) is
/// a host-supplied record:
/// several fields the host fills together. Typing a property as the kind nests those fields and
/// marks the group <c>authoredBy</c> that kind. The same kind may still sit on a TYPE as a marker,
/// so a game record with extra fields (layers, triggers) can be authored by pointing at one host
/// object.
/// </para>
/// </remarks>
public interface IHostKind;

// ---- marker kinds --------------------------------------------------------------------------

/// <summary>An object whose WORLD POSE is the value, baked by field name at export.</summary>
public readonly struct HostTransform : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Transform;
}

// ---- value kinds ---------------------------------------------------------------------------

/// <summary>A file on disk, authored through the host's file picker. The GUID of the picked
/// asset, from its sidecar.</summary>
public readonly record struct HostAsset : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Asset;

    /// <summary>The asset's authoring identity.</summary>
    public Guid Value { get; init; }
}

/// <summary>Another object in the scene, baked to its durable GUID at export.</summary>
public readonly record struct HostEntity : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Entity;

    /// <summary>The referenced object's identity — the same GUID its <c>meta</c> carries.</summary>
    public Guid Value { get; init; }
}

/// <summary>The host object's parent in the scene tree, baked to that parent's durable GUID.
/// Empty when the object is a root.</summary>
public readonly record struct HostParent : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Parent;

    /// <summary>The parent object's identity, or <see cref="Guid.Empty"/> for a root.</summary>
    public Guid Value { get; init; }
}

/// <summary>A renderable mesh, referenced by the mesh asset's GUID.</summary>
public readonly record struct HostMesh : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Mesh;

    /// <summary>The mesh asset's authoring identity.</summary>
    public Guid Value { get; init; }
}

/// <summary>A 2D billboard sprite, referenced by the sprite asset's GUID.</summary>
public readonly record struct HostSprite : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Sprite;

    /// <summary>The sprite asset's authoring identity.</summary>
    public Guid Value { get; init; }
}

/// <summary>The host object's own durable identity.</summary>
public readonly record struct HostId : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Id;

    /// <summary>The identity the host minted and stores for the object.</summary>
    public Guid Value { get; init; }
}

/// <summary>The host object's display name.</summary>
public readonly record struct HostName : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Name;

    /// <summary>What the author sees in the host's outliner.</summary>
    public string Value { get; init; }
}

/// <summary>The host object's LOCAL translation, engine convention (Y-up, metres).</summary>
public readonly record struct HostLocalPosition : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.LocalPosition;

    /// <summary>Local translation relative to the object's parent.</summary>
    public Vector3 Value { get; init; }
}

/// <summary>
/// The host object's LOCAL rotation — CANONICAL QUATERNION, always. A host with rotation modes
/// (Blender's euler orders, axis-angle) converts before supplying the value, so mode mess never
/// reaches a declaration.
/// </summary>
public readonly record struct HostLocalRotation : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.LocalRotation;

    /// <summary>Local rotation as a unit quaternion.</summary>
    public Quaternion Value { get; init; }
}

/// <summary>The host object's LOCAL scale.</summary>
public readonly record struct HostLocalScale : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.LocalScale;

    /// <summary>Local scale relative to the object's parent.</summary>
    public Vector3 Value { get; init; }
}

// ---- composed kinds ------------------------------------------------------------------------

/// <summary>Collision primitive kinds a host shape can bake. Member names match
/// <c>Paradise.Export.Data.PhysicsShapeType</c>; a separate type so Authoring never references
/// Export, and a distinct name so a file that imports both namespaces does not hit CS0104.</summary>
public enum HostShapeType
{
    Box,
    Sphere,
    Capsule,
}

/// <summary>
/// A collision shape, edited with the host's own handles. The geometric half of what a host
/// bakes — centre, rotation and the primitive's own extents.
/// </summary>
/// <remarks>
/// Layers, triggers and nav-carve flags are GAME statements, not host geometry, so they live on
/// the record that contains this rather than here. A collider list is then
/// <c>List&lt;HostShape&gt;</c>, or a game record with extra fields that sits
/// <c>[AuthoredByHost&lt;HostShape&gt;]</c> on the type and is filled by name.
/// </remarks>
public record struct HostShape : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Shape;

    /// <summary>Which primitive this is.</summary>
    public HostShapeType ShapeType { get; set; }

    /// <summary>The shape's origin relative to the object it is drawn on.</summary>
    public Vector3 LocalCenter { get; set; }

    /// <summary>The shape's orientation relative to the object it is drawn on.</summary>
    public Quaternion LocalRotation { get; set; } = Quaternion.Identity;

    /// <summary>Full size of a box. Unused for sphere and capsule.</summary>
    public Vector3 Size { get; set; }

    /// <summary>Radius of a sphere or capsule. Unused for a box.</summary>
    public float Radius { get; set; }

    /// <summary>Total height of a capsule, including hemispheres. Unused for box and sphere.</summary>
    public float Height { get; set; }

    /// <summary>Explicit so the initializers run for <c>new HostShape()</c>. <c>default(HostShape)</c>
    /// skips them, so a record property typed as this kind must be initialized <c>= new()</c> or an
    /// omitted object reads as all zeros. Publishable defaults also carry [AuthorDefault], because
    /// this type reaches a game's schema generator as metadata, where initializers are invisible.</summary>
    public HostShape() { }
}

/// <summary>How a host light shines.</summary>
public enum HostLightType
{
    Directional,
    Point,
    Spot,
}

/// <summary>
/// A light, whose colour, energy, shadows and aim are read at export. Direction comes from the
/// referenced object's orientation, which is why you aim a light by rotating it rather than by
/// typing a vector.
/// </summary>
public record struct HostLight : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Light;

    /// <summary>Directional, point or spot.</summary>
    public HostLightType Type { get; set; }

    /// <summary>World position. Unused for a directional, which is infinitely far away.</summary>
    public Vector3 Position { get; set; }

    /// <summary>The direction the light TRAVELS — from the source toward what it lights.</summary>
    public Vector3 Direction { get; set; }

    /// <summary>The light's colour, authored as a colour (Vector4 / {r,g,b,a}).</summary>
    public Vector4 Color { get; set; } = new(1f, 1f, 1f, 1f);

    /// <summary>Whether this light contributes.</summary>
    [AuthorDefault(true)]
    public bool Enabled { get; set; } = true;

    /// <summary>Energy. A directional's irradiance in W/m²; a point or spot's luminous intensity.</summary>
    [AuthorDefault(1f)]
    public float Intensity { get; set; } = 1f;

    /// <summary>Whether this light casts a shadow.</summary>
    public bool ShadowsEnabled { get; set; }

    /// <summary>How dark what it casts is; 1 is fully occluded.</summary>
    [AuthorDefault(1f)]
    public float ShadowStrength { get; set; } = 1f;

    /// <summary>Specular scale the host's light carries.</summary>
    public float Specular { get; set; }

    /// <summary>Angular size of a directional, or the emissive size of a point/spot.</summary>
    public float Size { get; set; }

    /// <summary>Reach of a point or spot. Unused for a directional.</summary>
    public float Range { get; set; }

    /// <summary>Full cone angle of a spot, in radians. Unused otherwise.</summary>
    public float SpotAngle { get; set; }

    /// <summary>Distance-falloff exponent of a point or spot. Unused for a directional.</summary>
    public float AttenuationExponent { get; set; }

    /// <summary>A white, enabled light of unit intensity that does not yet cast. Explicit so the
    /// initializers run for <c>new HostLight()</c>; <c>default(HostLight)</c> skips them, so a record
    /// property typed as this kind must be initialized <c>= new()</c>.</summary>
    public HostLight() { }
}

/// <summary>How a host camera projects.</summary>
public enum HostCameraProjection
{
    Perspective,
    Orthographic,
}

/// <summary>
/// A camera, whose lens and aim are read at export. Where it stands and which way it looks come
/// from the referenced object's pose, which is why you frame a shot by moving the camera rather
/// than by typing a vector.
/// </summary>
public record struct HostCamera : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Camera;

    /// <summary>Perspective or orthographic.</summary>
    [AuthorDefault(HostCameraProjection.Perspective)]
    public HostCameraProjection Projection { get; set; } = HostCameraProjection.Perspective;

    /// <summary>Vertical field of view, in degrees. Unused for orthographic.</summary>
    [AuthorDefault(50f)]
    public float Fov { get; set; } = 50f;

    /// <summary>Vertical size of an orthographic view, in metres. Unused for perspective.</summary>
    public float OrthographicSize { get; set; }

    /// <summary>Near clip plane, in metres.</summary>
    [AuthorDefault(0.1f)]
    public float Near { get; set; } = 0.1f;

    /// <summary>Far clip plane, in metres.</summary>
    [AuthorDefault(1000f)]
    public float Far { get; set; } = 1000f;

    /// <summary>World position of the camera object.</summary>
    public Vector3 Position { get; set; }

    /// <summary>World orientation of the camera object.</summary>
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    /// <summary>A 50° perspective, identity pose, clip 0.1–1000 — a host that omits a leaf
    /// means the common game camera, not a zero FOV that cannot frame anything. Explicit so the
    /// initializers run for <c>new HostCamera()</c>; <c>default(HostCamera)</c> skips them, so a
    /// record property typed as this kind must be initialized <c>= new()</c>.</summary>
    public HostCamera() { }
}
