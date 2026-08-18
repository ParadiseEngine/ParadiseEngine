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
