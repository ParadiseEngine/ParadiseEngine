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
/// link against these types (the pure-Python Blender addon) read. The JSON shape is unchanged by
/// the typed spelling.
/// </para>
/// <para>
/// Two families. A MARKER kind (<see cref="HostShape"/>, <see cref="HostMesh"/>, …) names a host
/// object the whole record or field is authored by pointing at; it carries no value of its own and
/// may sit on a type or a property. A VALUE kind (<see cref="HostId"/>,
/// <see cref="HostLocalPosition"/>, …) is one concrete value the host supplies; it binds a single
/// property — by attribute, or by typing the property as the kind itself — and declares the type
/// that property must have.
/// </para>
/// </remarks>
public interface IHostKind;

// ---- marker kinds --------------------------------------------------------------------------

/// <summary>A collision shape, edited with the host's own handles.</summary>
public readonly struct HostShape : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Shape;
}

/// <summary>A renderable mesh, whose source asset is resolved at export.</summary>
public readonly struct HostMesh : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Mesh;
}

/// <summary>A 2D billboard sprite, whose sheet and quad geometry are read at export.</summary>
public readonly struct HostSprite : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Sprite;
}

/// <summary>A light, whose colour, energy, shadows and aim are read at export.</summary>
public readonly struct HostLight : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Light;
}

/// <summary>An object whose WORLD POSE is the value, baked by field name at export.</summary>
public readonly struct HostTransform : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Transform;
}

// ---- value kinds ---------------------------------------------------------------------------

/// <summary>A file on disk, authored through the host's file picker.</summary>
public readonly record struct HostAsset : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Asset;

    /// <summary>The project-relative path the picker resolved.</summary>
    public string? Value { get; init; }
}

/// <summary>Another object in the scene, baked to its NAME at export.</summary>
public readonly record struct HostEntity : IHostKind
{
    /// <summary>The <c>authoredBy</c> string this kind publishes.</summary>
    public const string Kind = AuthoredBySources.Entity;

    /// <summary>The referenced object's name.</summary>
    public string? Value { get; init; }
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
