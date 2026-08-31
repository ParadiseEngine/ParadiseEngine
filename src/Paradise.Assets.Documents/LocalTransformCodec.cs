using System.Numerics;

namespace Paradise.Assets.Documents;

/// <summary>
/// A local TRS, in engine convention: right-handed Y-up, metres, quaternion as
/// <c>[x, y, z, w]</c>.
/// </summary>
public readonly record struct LocalTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    /// <summary>No translation, no rotation, unit scale.</summary>
    public static LocalTransform Identity { get; } = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
}

/// <summary>
/// Reading and writing the well-known <c>transform</c> component as a <see cref="LocalTransform"/> —
/// the typed form of <see cref="WellKnownComponents.TransformId"/>, so no consumer hand-parses
/// <c>Position</c>/<c>Rotation</c>/<c>Scale</c> tables.
/// </summary>
/// <remarks>
/// Reading is lenient per FIELD: an absent field is that part of the identity, because an
/// authored transform may legitimately say only what differs from it. Malformed fields cannot
/// arrive from disk — <see cref="WellKnownComponents.PayloadProblem"/> refuses them at parse —
/// so a wrong shape here also reads as the identity rather than throwing on an in-memory table.
/// </remarks>
public static class LocalTransformCodec
{
    /// <summary>The transform a component's payload declares; absent fields are the identity's.</summary>
    public static LocalTransform Read(CanonicalTomlTable data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new LocalTransform(
            Vector(data.Value(WellKnownComponents.Position), Vector3.Zero),
            Quat(data.Value(WellKnownComponents.Rotation)),
            Vector(data.Value(WellKnownComponents.Scale), Vector3.One));
    }

    /// <summary>
    /// Renders <paramref name="transform"/> as a full <c>transform</c> component — all three
    /// fields, in canonical field order.
    /// </summary>
    public static PrefabComponent Write(LocalTransform transform)
        => new(WellKnownComponents.TransformId, WellKnownComponents.TransformType,
            new CanonicalTomlTable
            {
                {
                    WellKnownComponents.Position,
                    new object[] { (double)transform.Position.X, (double)transform.Position.Y, (double)transform.Position.Z }
                },
                {
                    WellKnownComponents.Rotation,
                    new object[] { (double)transform.Rotation.X, (double)transform.Rotation.Y, (double)transform.Rotation.Z, (double)transform.Rotation.W }
                },
                {
                    WellKnownComponents.Scale,
                    new object[] { (double)transform.Scale.X, (double)transform.Scale.Y, (double)transform.Scale.Z }
                },
            });

    private static Vector3 Vector(object? value, Vector3 fallback)
    {
        if (value is not IReadOnlyList<object> numbers || numbers.Count != 3) return fallback;
        return new Vector3(Single(numbers[0]), Single(numbers[1]), Single(numbers[2]));
    }

    private static Quaternion Quat(object? value)
    {
        if (value is not IReadOnlyList<object> numbers || numbers.Count != 4) return Quaternion.Identity;
        return new Quaternion(Single(numbers[0]), Single(numbers[1]), Single(numbers[2]), Single(numbers[3]));
    }

    private static float Single(object value) => Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
}
