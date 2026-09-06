using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Authoring;
using Paradise.Export.Data;

// The test assembly IS a game here: since contract v6 the engine declares no authored
// components, so these fixtures — declared, generated and registered exactly the way a game's
// records are — are what the router, reader and golden tests exercise the mechanism with.
[assembly: AuthoredRegistry]

namespace Paradise.Export.Tests;

/// <summary>The fixture component ids, pinned as constants so tests assert against one spelling.</summary>
public static class TestComponentIds
{
    public const string Mover = "a0000000-0000-4000-8000-000000000001";
    public const string Glow = "a0000000-0000-4000-8000-000000000002";
    public const string Crate = "a0000000-0000-4000-8000-000000000003";

    public static readonly Guid MoverId = new(Mover);
    public static readonly Guid GlowId = new(Glow);
    public static readonly Guid CrateId = new(Crate);
}

/// <summary>Scalar shapes: enum by name, float, string, bool — the common component diet.</summary>
[Guid(TestComponentIds.Mover)]
[Authored(DisplayName = "Mover")]
public sealed record MoverFixture
{
    public MoverKind Kind { get; set; }
    public float Mass { get; set; } = 1f;
    public float MoveSpeed { get; set; }
    public string? Clip { get; set; }
    public bool Active { get; set; } = true;
}

public enum MoverKind
{
    Static,
    Dynamic,
}

/// <summary>Nullable value-type leaves: present materializes, absent (or JSON null) stays null.</summary>
[Guid(TestComponentIds.Glow)]
[Authored(DisplayName = "Glow")]
public sealed record GlowFixture
{
    public int? ShadowMapSize { get; set; }
    public float? ShadowBlur { get; set; }
}

/// <summary>A composed LIST whose element is a host-baked shape: a GAME's collider-shape record,
/// as the engine declares none of its own. Kept as coverage for list-of-composed reading.</summary>
[Guid(TestComponentIds.Crate)]
[Authored(DisplayName = "Crate")]
public sealed record CrateFixture
{
    public List<CrateShapeFixture> Colliders { get; set; } = new();
}

/// <summary>The shape row a game would declare: <see cref="HostShape"/>'s geometry by name, plus
/// the game's own flags.</summary>
[AuthoredByHost<HostShape>]
public sealed record CrateShapeFixture
{
    public string? Id { get; set; }
    public bool IsStatic { get; set; }
    public bool IsTrigger { get; set; }
    public PhysicsShapeType ShapeType { get; set; }
    public Vector3 LocalCenter { get; set; } = Vector3.Zero;
    public Quaternion LocalRotation { get; set; } = Quaternion.Identity;
    public Vector3 Size { get; set; } = Vector3.Zero;
    public float Radius { get; set; }
    public float Height { get; set; }
}

/// <summary>The test assembly's own generated registry — the exact mechanism a game uses.</summary>
public static class TestRegistry
{
    public static IAuthoredComponentRegistry Default => global::Paradise.Export.Test.AuthoredComponents.Default;
}

/// <summary>A component read through a HAND-WRITTEN registry — deliberately not [Authored], so
/// the router sees it only through the interface a game could implement by hand.</summary>
public sealed record LedgeFixture
{
    public float Friction { get; set; }
    public bool IsTrigger { get; set; }
    public string Label { get; set; } = "";
}

[System.Text.Json.Serialization.JsonSerializable(typeof(LedgeFixture))]
internal sealed partial class LedgeFixtureJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
