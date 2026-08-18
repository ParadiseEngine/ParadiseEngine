using System.Numerics;
using Paradise.Authoring;

namespace Paradise.Authoring.Test;

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
[AuthorNativeShape]
public sealed record SampleColliderFixture
{
    [Meters] public float SizeX { get; set; } = 1f;
    [Meters] public float SizeY { get; set; } = 1f;
    [Meters] public float SizeZ { get; set; } = 1f;
}

/// <summary>Exercises every schema feature at once: units, advisory ranges, docs, defaults of
/// three different types, an enum, composition, and a declared gizmo.</summary>
[Authored("test.everything", DisplayName = "Everything")]
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

/// <summary>A second component, declared out of alphabetical order relative to the first, so the
/// ordering guarantee is actually tested.</summary>
[Authored("test.a-minimal")]
public sealed record MinimalFixture
{
    public float Value { get; set; } = 1f;
}


/// <summary>A part authored by pointing at one shape; the fields are what gets baked out of it.</summary>
[AuthoredByHost(AuthoredBySources.Shape)]
public sealed record ShapeRefFixture
{
    public SampleShape Kind { get; set; } = SampleShape.Box;
    public Vector3 Size { get; set; }
    public Vector3 LocalCenter { get; set; }
    public Quaternion LocalRotation { get; set; }
    public float Radius { get; set; }
}

/// <summary>Everything schema v2 added, in one component: a LIST of shape references, the fixed-size
/// aggregates, an asset reference with its accepted kinds, and two fields guarded by siblings.</summary>
[Authored("test.v2", DisplayName = "Schema v2")]
public sealed record V2Fixture
{
    /// <summary>An array of host-object references — the shape the engine's collider list has.</summary>
    public List<ShapeRefFixture> Colliders { get; set; } = new();

    [AuthoredByHost(AuthoredBySources.Mesh)]
    public string MeshNode { get; set; } = "";

    [AuthoredByHost(AuthoredBySources.Asset), AuthorAssetKinds(".glb", ".gltf")]
    public string Model { get; set; } = "";

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


/// <summary>A whole component authored by pointing at ONE host object — the shape the engine's
/// sprite animation has, where sheet, grid and quad size are all read off the sprite.</summary>
[Authored("test.by-sprite", DisplayName = "By sprite")]
[AuthoredByHost(AuthoredBySources.Sprite)]
public sealed record BySpriteFixture
{
    public string? Sheet { get; set; }
    public Vector2 QuadSize { get; set; }
}
