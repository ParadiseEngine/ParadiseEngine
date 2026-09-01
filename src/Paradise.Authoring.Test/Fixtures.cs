using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Authoring;

namespace Paradise.Authoring.Test;

/// <summary>
/// The ids the fixtures below are authored under.
///
/// Constants because a <c>[Guid]</c> argument must be one, and because a test that asserts against
/// a GUID typed out a second time is a test that eventually asserts against a typo.
/// </summary>
public static class FixtureIds
{
    public const string Everything = "e0000000-0000-4000-8000-000000000001";
    public const string Minimal = "e0000000-0000-4000-8000-000000000002";
    public const string V2 = "e0000000-0000-4000-8000-000000000003";
    public const string BySprite = "e0000000-0000-4000-8000-000000000004";
    public const string HostBound = "e0000000-0000-4000-8000-000000000005";
    public const string ByLight = "e0000000-0000-4000-8000-000000000006";
    public const string ByCamera = "e0000000-0000-4000-8000-000000000007";

    public static readonly Guid HostBoundId = new(HostBound);
    public static readonly Guid EverythingId = new(Everything);
    public static readonly Guid MinimalId = new(Minimal);
    public static readonly Guid V2Id = new(V2);
    public static readonly Guid BySpriteId = new(BySprite);
    public static readonly Guid ByLightId = new(ByLight);
    public static readonly Guid ByCameraId = new(ByCamera);
}

/// <summary>An enum the schema has to describe by NAME, matching how the export contract
/// serializes enums.</summary>
public enum SampleShape
{
    Box,
    Sphere,
    Capsule,
}

/// <summary>A part, not a component: no id of its own, authored by pointing at the host's own
/// shape object rather than by typing these numbers.</summary>
[AuthoredByHost<HostShape>]
public sealed record SampleColliderFixture
{
    [Meters] public float SizeX { get; set; } = 1f;
    [Meters] public float SizeY { get; set; } = 1f;
    [Meters] public float SizeZ { get; set; } = 1f;
}

/// <summary>Exercises every schema feature at once: units, advisory ranges, docs, defaults of
/// three different types, an enum, composition, and a declared gizmo.</summary>
[Guid(FixtureIds.Everything)]
[Authored(DisplayName = "Everything")]
[AuthorBoxGizmo(nameof(HalfExtentX), nameof(HalfExtentZ), nameof(Depth))]
public sealed record EverythingFixture
{
    [Meters, AuthorRange(1, 100), AuthorDoc("Half-width on X.")]
    public float HalfExtentX { get; set; } = 9f;

    [Meters] public float HalfExtentZ { get; set; } = 6f;
    [Meters] public float Depth { get; set; } = 4f;

    [Seconds] public float Duration { get; set; } = 2.5f;
    [Radians] public float Heading { get; set; }
    [Kilograms] public float Mass { get; set; } = 1f;
    [Unit01] public float Friction { get; set; } = 0.35f;

    public int Count { get; set; } = 5;
    public string Label { get; set; } = "unnamed";

    /// <summary>False on purpose: a bool default that a numeric-only encoder turns into 0.</summary>
    public bool IsTrigger { get; set; }

    public SampleShape Shape { get; set; } = SampleShape.Capsule;

    /// <summary>Composition — the schema must nest rather than flatten.</summary>
    public SampleColliderFixture Box { get; set; } = new();
}

/// <summary>A second component, whose TYPE NAME sorts before the first's, so the ordering
/// guarantee is actually tested.</summary>
[Guid(FixtureIds.Minimal)]
[Authored]
public sealed record MinimalFixture
{
    public float Value { get; set; } = 1f;
}


/// <summary>A part authored by pointing at one shape; the fields are what gets baked out of it.</summary>
[AuthoredByHost<HostShape>]
public sealed record ShapeRefFixture
{
    public SampleShape Kind { get; set; } = SampleShape.Box;
    public Vector3 Size { get; set; }
    public Vector3 LocalCenter { get; set; }
    public Quaternion LocalRotation { get; set; }
    public float Radius { get; set; }
}

/// <summary>A part authored by pointing at an object and reading WHERE IT STANDS. Declares only
/// two of the four pose fields an exporter knows how to bake, which is the point: a record takes
/// the part of the pose it means and an exporter fills what it finds.</summary>
[AuthoredByHost<HostTransform>]
public sealed record PlacementRefFixture
{
    public Vector3 Position { get; set; }
    [Radians] public float Yaw { get; set; }
}

/// <summary>Everything schema v2 added, in one component: a LIST of shape references, the fixed-size
/// aggregates, an asset reference with its accepted kinds, and two fields guarded by siblings.</summary>
[Guid(FixtureIds.V2)]
[Authored(DisplayName = "Schema v2")]
public sealed record V2Fixture
{
    /// <summary>An array of host-object references — the shape the engine's collider list has.</summary>
    public List<ShapeRefFixture> Colliders { get; set; } = new();

    /// <summary>A single host-object reference whose value is a POSE, not a shape or an asset.</summary>
    public PlacementRefFixture Destination { get; set; } = new();

    [AuthoredByHost<HostMesh>]
    public Guid MeshNode { get; set; }

    [AuthoredByHost<HostAsset>, AuthorAssetKinds(".glb", ".gltf")]
    public Guid Model { get; set; }

    public Vector2 QuadSize { get; set; }
    public Vector3 Offset { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector4 Tint { get; set; }

    /// <summary>Unsigned in C#, plain int in the schema — no editor has a separate control.</summary>
    public uint Seed { get; set; } = 1;

    public bool IsAgent { get; set; }

    /// <summary>Hidden unless IsAgent — what EntityExport did in _ValidateProperty, as data.</summary>
    [AuthorVisibleWhen(nameof(IsAgent), true)]
    public float MoveSpeed { get; set; } = 1.4f;

    public SampleShape Shape { get; set; } = SampleShape.Box;

    /// <summary>Guarded by an ENUM sibling, compared by name.</summary>
    [AuthorVisibleWhen(nameof(Shape), SampleShape.Sphere)]
    public float SphereRadius { get; set; } = 0.5f;
}


/// <summary>A sprite referenced by the sprite asset's GUID — sheet and quad size stay ordinary
/// fields, because a GUID is one value and cannot carry the rest.</summary>
[Guid(FixtureIds.BySprite)]
[Authored(DisplayName = "By sprite")]
public sealed record BySpriteFixture
{
    [AuthoredByHost<HostSprite>]
    public Guid Sheet { get; set; }

    public Vector2 QuadSize { get; set; }
}

/// <summary>A whole component authored by pointing at ONE host light — the shape a directional
/// or lamp uses, where colour and energy are read off the light.</summary>
[Guid(FixtureIds.ByLight)]
[Authored(DisplayName = "By light")]
[AuthoredByHost<HostLight>]
public sealed record ByLightFixture
{
    public Vector3 Direction { get; set; }
    public float Intensity { get; set; } = 1f;
}

/// <summary>A whole component authored by pointing at ONE host camera — the shape a shot uses,
/// where the lens and aim are read off the camera object.</summary>
[Guid(FixtureIds.ByCamera)]
[Authored(DisplayName = "By camera")]
[AuthoredByHost<HostCamera>]
public sealed record ByCameraFixture
{
    public float Fov { get; set; } = 50f;
    public Vector3 Position { get; set; }
}

/// <summary>
/// Every VALUE host kind, bound both ways: by attribute (the field keeps its wire type, which
/// PAUT010 checks against the kind's) and by type (the field IS the kind, and the generated
/// reader wraps the wire value back into it).
/// </summary>
[Guid(FixtureIds.HostBound)]
[Authored(DisplayName = "Host bound")]
public sealed record HostBoundFixture
{
    [AuthoredByHost<HostId>]
    public Guid Ident { get; set; }

    [AuthoredByHost<HostName>]
    public string Label { get; set; } = "";

    [AuthoredByHost<HostLocalRotation>]
    public Quaternion Spin { get; set; }

    [AuthoredByHost<HostParent>]
    public Guid Parent { get; set; }

    [AuthoredByHost<HostEntity>]
    public Guid Target { get; set; }

    [AuthoredByHost<HostAsset>]
    public Guid File { get; set; }

    [AuthoredByHost<HostMesh>]
    public Guid Mesh { get; set; }

    [AuthoredByHost<HostSprite>]
    public Guid Sprite { get; set; }

    public HostLocalPosition Position { get; set; }

    public HostLocalScale Scale { get; set; }

    public HostShape Collider { get; set; }

    public HostLight Lamp { get; set; }

    public HostCamera Eye { get; set; }
}
