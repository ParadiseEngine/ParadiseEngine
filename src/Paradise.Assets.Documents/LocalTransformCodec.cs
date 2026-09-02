using System.Numerics;

namespace Paradise.Assets.Documents;

/// <summary>Engine convention: right-handed Y-up, metres, quaternion as <c>[x, y, z, w]</c>.</summary>
public readonly record struct LocalTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static LocalTransform Identity { get; } = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
}

/// <summary>The typed form of the <c>transform</c> component. An absent field is that part of the identity; malformed shapes are refused at parse, so an in-memory one also reads as identity rather than throwing.</summary>
public static class LocalTransformCodec
{
    public static LocalTransform Read(CanonicalTomlTable data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new LocalTransform(
            Vector(data.Value(WellKnownComponents.Position), Vector3.Zero),
            Quat(data.Value(WellKnownComponents.Rotation)),
            Vector(data.Value(WellKnownComponents.Scale), Vector3.One));
    }

    /// <summary>All three fields, in canonical order. The floats are handed to the table as float32 so its widening (<see cref="CanonicalFloat"/>) spells them.</summary>
    public static PrefabComponent Write(LocalTransform transform)
        => new(WellKnownComponents.TransformId, WellKnownComponents.TransformType,
            new CanonicalTomlTable
            {
                {
                    WellKnownComponents.Position,
                    new object[] { transform.Position.X, transform.Position.Y, transform.Position.Z }
                },
                {
                    WellKnownComponents.Rotation,
                    new object[] { transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W }
                },
                {
                    WellKnownComponents.Scale,
                    new object[] { transform.Scale.X, transform.Scale.Y, transform.Scale.Z }
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
