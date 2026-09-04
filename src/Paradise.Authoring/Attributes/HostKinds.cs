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

/// <summary>
/// A spritesheet animation's GEOMETRY, read off the host's own sprite object: which sheet, how it
/// is divided, how big one frame is in the world, and whether it faces the camera.
/// </summary>
/// <remarks>
/// The playback CLOCK is not here. Frame count, rate and looping are statements about how the
/// animation runs, and no sprite object holds them — so they stay ordinary authored fields on the
/// record that contains this, exactly as layers and triggers stay off <see cref="HostShape"/>.
///
/// Distinct from <see cref="HostSprite"/>, which is the value kind for "which sheet" alone. Both
/// exist because they answer different questions: a record that only needs the asset takes the
/// value kind and keeps its own quad size; a record that wants the host to divide the sheet for it
/// takes this.
/// </remarks>
public record struct HostSpriteSheet : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.SpriteSheet;

    /// <summary>Which sheet, by the asset's identity.</summary>
    /// <remarks>Typed as <see cref="HostSprite"/> rather than restating a GUID, so this reference
    /// is the same thing as a standalone one: the schema publishes it <c>authoredBy: sprite</c> and
    /// an editor draws its sprite picker instead of a text box. A host that still bakes a runtime
    /// PATH says so on its own record, where PAUT010's baked-path allowance covers mesh, sprite and
    /// asset; a field inside this kind is not that case and does not get the hatch.</remarks>
    public HostSprite Sheet { get; set; }

    /// <summary>Frame columns across the sheet.</summary>
    /// <remarks>The LOWER bound is the real one: this divides the sheet, so a zero reaches whatever
    /// computes a frame rectangle. The ceiling is a sanity check — a sheet cut finer than this has
    /// frames of a few pixels on the largest texture a GPU will hold.</remarks>
    [AuthorDefault(1), AuthorRange(1, 4096)]
    public int Columns { get; set; } = 1;

    /// <summary>Frame rows down the sheet.</summary>
    [AuthorDefault(1), AuthorRange(1, 4096)]
    public int Rows { get; set; } = 1;

    /// <summary>World size of ONE frame's quad, in metres.</summary>
    public Vector2 QuadSize { get; set; } = Vector2.One;

    /// <summary>Whether the quad turns to face the camera.</summary>
    [AuthorDefault(true)]
    public bool Billboard { get; set; } = true;

    /// <summary>A 1×1 unit billboard with no sheet. Explicit so the initializers run for
    /// <c>new HostSpriteSheet()</c>; <c>default(HostSpriteSheet)</c> skips them, so a record
    /// property typed as this kind must be initialized <c>= new()</c>.</summary>
    public HostSpriteSheet() { }
}

/// <summary>Where a scene's ambient light comes from.</summary>
public enum HostAmbientMode
{
    /// <summary>One flat colour.</summary>
    Color,

    /// <summary>The sky, integrated per zone into <see cref="HostEnvironment.AmbientSky"/>,
    /// <see cref="HostEnvironment.AmbientEquator"/> and <see cref="HostEnvironment.AmbientGround"/>.</summary>
    Sky,
}

/// <summary>
/// A tone-mapping operator, by the name the OPERATOR has in the literature rather than the name any
/// one host spells it with — a host that calls Reinhard "Reinhardt" maps to this on the way out.
/// </summary>
public enum HostTonemapMode
{
    Linear,
    Reinhard,
    Filmic,
    Aces,
    AgX,
}

/// <summary>
/// The scene's lighting mood, read off whatever object the host keeps it on — ambient, background,
/// fog, tone mapping, and the two screen-space effects a scene turns on rather than tunes.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a sky IS, never one renderer's fit to it.</b> The gradient below — four colours and two
/// curves — is the procedural-sky model every host has some form of. The cosine thresholds and
/// shader constants of a PARTICULAR sky are not, and are deliberately absent: a runtime that wants
/// a sun disk derives it from the directional light's own <see cref="HostLight.Size"/> rather than
/// from constants only one editor knows how to produce.
/// </para>
/// <para>
/// Spherical-harmonic ambient is absent for the same reason, and it is the closest call here. A
/// host that integrates its own sky more accurately than a gradient can express has a real result
/// to publish — but SH has several mutually incompatible conventions (band count, coefficient
/// order, whether the band factors are premultiplied, whether the normalization is E or E/π), and a
/// kind that named one of them would be publishing a renderer's agreement as though it were a
/// property of skies. A game that wants it declares it on its OWN record, where the convention it
/// means is the convention its own runtime reads.
/// </para>
/// <para>
/// The alternative, rejected: carrying the host's shader constants verbatim. It would have kept one
/// editor's sky pixel-exact and made the kind unimplementable by every other host, which is the
/// failure mode <see cref="AuthoredByHostAttribute{THost}"/> exists to prevent.
/// </para>
/// </remarks>
public record struct HostEnvironment : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Environment;

    /// <summary>Whether ambient is a flat colour or comes from the sky.</summary>
    [AuthorDefault(HostAmbientMode.Color)]
    public HostAmbientMode AmbientMode { get; set; } = HostAmbientMode.Color;

    /// <summary>Ambient arriving on an up-facing surface, or the flat colour when
    /// <see cref="AmbientMode"/> is <see cref="HostAmbientMode.Color"/>. Linear, not sRGB.</summary>
    public Vector4 AmbientSky { get; set; } = new(0.5f, 0.52f, 0.56f, 1f);

    /// <summary>Ambient arriving on a horizontal-facing surface. Linear.</summary>
    public Vector4 AmbientEquator { get; set; } = new(0.5f, 0.52f, 0.56f, 1f);

    /// <summary>Ambient arriving on a down-facing surface. Linear.</summary>
    public Vector4 AmbientGround { get; set; } = new(0.2f, 0.19f, 0.18f, 1f);

    /// <summary>Multiplier on the ambient term.</summary>
    [AuthorDefault(1f)]
    public float AmbientEnergy { get; set; } = 1f;

    /// <summary>Whether the sky contributes ambient SPECULAR as well as diffuse.</summary>
    public bool SkyReflections { get; set; }

    /// <summary>Whether this scene states a background at all. False leaves the renderer's own.</summary>
    /// <remarks>The contract cannot otherwise distinguish "authored exactly these values" from
    /// "nobody authored an environment", and a renderer with a tuned default must not have it
    /// silently flattened by a scene that never spoke.</remarks>
    public bool HasBackground { get; set; }

    /// <summary>The clear colour, when <see cref="SkyGradient"/> is off. Linear.</summary>
    public Vector4 BackgroundColor { get; set; } = new(0.5f, 0.52f, 0.56f, 1f);

    /// <summary>Whether the background is the gradient below rather than a flat colour.</summary>
    public bool SkyGradient { get; set; }

    /// <summary>Gradient zenith. Linear.</summary>
    public Vector4 SkyTop { get; set; } = new(0.03f, 0.024f, 0.016f, 1f);

    /// <summary>Gradient sky-side horizon. Linear.</summary>
    public Vector4 SkyHorizon { get; set; } = new(0.2f, 0.2f, 0.21f, 1f);

    /// <summary>Gradient ground-side horizon. Linear.</summary>
    public Vector4 GroundHorizon { get; set; } = new(0.2f, 0.2f, 0.21f, 1f);

    /// <summary>Gradient nadir. Linear.</summary>
    public Vector4 GroundBottom { get; set; } = new(0.03f, 0.024f, 0.016f, 1f);

    /// <summary>
    /// How fast the sky half of the gradient falls from zenith to horizon. SMALLER is tighter to
    /// the horizon.
    /// </summary>
    /// <remarks>
    /// The curve a sky HAS, not an exponent fitted to any one shader. A renderer that wants a
    /// <c>pow()</c> exponent inverts this itself, which is the same division it would already be
    /// doing — and putting the inverted form here instead would be exactly the renderer's-fit
    /// problem this kind exists to keep out, with the added trap that the field would then share a
    /// name with a host property holding its reciprocal.
    /// </remarks>
    [AuthorDefault(0.15f)]
    public float SkyCurve { get; set; } = 0.15f;

    /// <summary>The same, for the ground half.</summary>
    [AuthorDefault(0.02f)]
    public float GroundCurve { get; set; } = 0.02f;

    /// <summary>Which operator maps HDR to display.</summary>
    [AuthorDefault(HostTonemapMode.Linear)]
    public HostTonemapMode TonemapMode { get; set; } = HostTonemapMode.Linear;

    /// <summary>Exposure applied before the operator.</summary>
    [AuthorDefault(1f)]
    public float TonemapExposure { get; set; } = 1f;

    /// <summary>The luminance the operator maps to white.</summary>
    [AuthorDefault(1f)]
    public float TonemapWhite { get; set; } = 1f;

    /// <summary>Whether distance fog contributes.</summary>
    public bool FogEnabled { get; set; }

    /// <summary>Fog colour. Linear.</summary>
    public Vector4 FogColor { get; set; } = new(0.5f, 0.52f, 0.56f, 1f);

    /// <summary>Fog density per metre.</summary>
    public float FogDensity { get; set; }

    /// <summary>Whether screen-space ambient occlusion contributes.</summary>
    public bool SsaoEnabled { get; set; }

    /// <summary>Occlusion sampling radius, in metres.</summary>
    [AuthorDefault(1f)]
    public float SsaoRadius { get; set; } = 1f;

    /// <summary>Occlusion strength.</summary>
    [AuthorDefault(2f)]
    public float SsaoIntensity { get; set; } = 2f;

    /// <summary>Exponent applied to the occlusion term.</summary>
    [AuthorDefault(1.5f)]
    public float SsaoPower { get; set; } = 1.5f;

    /// <summary>Whether bloom contributes.</summary>
    public bool GlowEnabled { get; set; }

    /// <summary>Bloom strength.</summary>
    [AuthorDefault(1f)]
    public float GlowIntensity { get; set; } = 1f;

    /// <summary>The HDR luminance above which a pixel blooms.</summary>
    [AuthorDefault(1f)]
    public float GlowThreshold { get; set; } = 1f;

    /// <summary>Per-layer shadow map resolution the scene asks for, in texels. Absent leaves the
    /// renderer's own.</summary>
    /// <remarks>The odd member of this kind: it sizes a GPU resource rather than describing how the
    /// scene LOOKS. It stays because a scene is where an author decides how much shadow detail to
    /// pay for, and there is no other authored thing for it to hang on.</remarks>
    public int? ShadowMapSize { get; set; }

    /// <summary>Soft-shadow PCF disk radius in shadow texels — the penumbra width of every shadow
    /// edge. Absent leaves the renderer's own.</summary>
    public float? ShadowBlur { get; set; }

    /// <summary>A neutral grey ambient with no background, no fog and no effects — a scene that
    /// authored an environment and changed nothing. Explicit so the initializers run for
    /// <c>new HostEnvironment()</c>; <c>default(HostEnvironment)</c> skips them, so a record
    /// property typed as this kind must be initialized <c>= new()</c>.</summary>
    public HostEnvironment() { }
}
