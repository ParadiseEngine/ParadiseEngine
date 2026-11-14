using System.Numerics;

namespace Paradise.Rendering.Sdf;

/// <summary>A scene's shapes validated and packed once, evaluable on the CPU.</summary>
/// <remarks>The renderer uploads exactly <see cref="Shapes"/>, and <see cref="Distance"/> folds
/// them like the shader does, so picking, collision probes and tests see the field the GPU traces.
/// Storage is reused across <see cref="Set"/> calls; instances are not thread-safe.</remarks>
public sealed class SdfField
{
    /// <summary>How far a transform's axes may differ in length, relative to their scale, before
    /// it stops counting as a uniform scale.</summary>
    private const float SimilarityTolerance = 1e-3f;

    private SdfShapeGpu[] _shapes = [];

    public int Count { get; private set; }

    /// <summary>The packed shapes in fold order, in the layout the shader reads.</summary>
    public ReadOnlySpan<SdfShapeGpu> Shapes => _shapes.AsSpan(0, Count);

    /// <summary>Replaces the field's shapes.</summary>
    /// <remarks>Packing overwrites the previous shapes in place, so a rejected scene leaves the
    /// field empty rather than holding half of two scenes.</remarks>
    /// <exception cref="ArgumentException">A shape's transform is not a finite, invertible
    /// rotation, translation and uniform scale, or its size is not finite.</exception>
    public void Set(ReadOnlySpan<SdfShape> shapes)
    {
        if (_shapes.Length < shapes.Length) _shapes = new SdfShapeGpu[Math.Max(shapes.Length, _shapes.Length * 2)];
        Count = 0;
        for (var i = 0; i < shapes.Length; i++) _shapes[i] = Pack(in shapes[i], i);
        Count = shapes.Length;
    }

    /// <summary>The signed distance from <paramref name="point"/> to the field's surface.</summary>
    public float Distance(Vector3 point)
    {
        var distance = SdfMath.Far;
        foreach (ref readonly var shape in Shapes)
        {
            var smoothness = shape.Params.Z;
            if (CanSkip(in shape, point, distance, smoothness)) continue;
            distance = SdfMath.Combine((SdfOperation)shape.Params.Y, distance, Evaluate(in shape, point), smoothness, out _);
        }
        return distance;
    }

    /// <summary>The signed distance and the blended material at <paramref name="point"/>.</summary>
    public float Sample(Vector3 point, out SdfMaterial material)
    {
        var distance = SdfMath.Far;
        var albedoRoughness = Vector4.Zero;
        var emissiveMetallic = Vector4.Zero;
        foreach (ref readonly var shape in Shapes)
        {
            var smoothness = shape.Params.Z;
            if (CanSkip(in shape, point, distance, smoothness)) continue;
            distance = SdfMath.Combine((SdfOperation)shape.Params.Y, distance, Evaluate(in shape, point), smoothness, out var weight);
            albedoRoughness = albedoRoughness * (1f - weight) + shape.AlbedoRoughness * weight;
            emissiveMetallic = emissiveMetallic * (1f - weight) + shape.EmissiveMetallic * weight;
        }
        material = new SdfMaterial(
            new Vector3(albedoRoughness.X, albedoRoughness.Y, albedoRoughness.Z), albedoRoughness.W, emissiveMetallic.W,
            new Vector3(emissiveMetallic.X, emissiveMetallic.Y, emissiveMetallic.Z));
        return distance;
    }

    /// <summary>The field's unit gradient at <paramref name="point"/>, from four tetrahedral taps.</summary>
    public Vector3 Normal(Vector3 point, float epsilon = 1e-3f)
    {
        var a = new Vector3(1f, -1f, -1f);
        var b = new Vector3(-1f, -1f, 1f);
        var c = new Vector3(-1f, 1f, -1f);
        var d = Vector3.One;
        var gradient = a * Distance(point + a * epsilon) + b * Distance(point + b * epsilon)
            + c * Distance(point + c * epsilon) + d * Distance(point + d * epsilon);
        var length = gradient.Length();
        return length > 0f ? gradient / length : Vector3.UnitY;
    }

    /// <summary>Sphere-traces a ray with the renderer's stepping and hit rule.</summary>
    /// <param name="direction">Unit length.</param>
    /// <param name="distance">Distance along the ray to the hit.</param>
    public bool TryRaycast(
        Vector3 origin, Vector3 direction, float maxDistance, out float distance,
        int maxSteps = 128, float surfaceEpsilon = 1e-3f)
    {
        var travelled = 0f;
        for (var step = 0; step < maxSteps; step++)
        {
            var toSurface = Distance(origin + direction * travelled);
            if (toSurface < surfaceEpsilon * (1f + travelled))
            {
                distance = travelled;
                return true;
            }
            travelled += toSurface;
            if (travelled > maxDistance) break;
        }
        distance = 0f;
        return false;
    }

    /// <summary>A union cannot change the result while the shape's bounding sphere is farther
    /// than the accumulated distance plus the blend radius.</summary>
    private static bool CanSkip(in SdfShapeGpu shape, Vector3 point, float distance, float smoothness)
    {
        if ((SdfOperation)shape.Params.Y != SdfOperation.Union) return false;
        var center = new Vector3(shape.Bounds.X, shape.Bounds.Y, shape.Bounds.Z);
        return (point - center).Length() - shape.Bounds.W >= distance + smoothness;
    }

    private static float Evaluate(in SdfShapeGpu shape, Vector3 point)
    {
        var local = Vector3.Transform(point, shape.WorldToLocal);
        return SdfMath.Primitive((SdfShapeKind)shape.Params.X, local, shape.Size) * shape.Params.W;
    }

    private static SdfShapeGpu Pack(in SdfShape shape, int index)
    {
        var transform = shape.Transform;
        var x = new Vector3(transform.M11, transform.M12, transform.M13);
        var y = new Vector3(transform.M21, transform.M22, transform.M23);
        var z = new Vector3(transform.M31, transform.M32, transform.M33);
        var scale = x.Length();
        var skew = SimilarityTolerance * scale * scale;
        if (!SdfMath.IsFinite(transform) || transform.M14 != 0f || transform.M24 != 0f || transform.M34 != 0f || transform.M44 != 1f
            || !(scale > 0f)
            || MathF.Abs(y.Length() - scale) > SimilarityTolerance * scale
            || MathF.Abs(z.Length() - scale) > SimilarityTolerance * scale
            || MathF.Abs(Vector3.Dot(x, y)) > skew || MathF.Abs(Vector3.Dot(y, z)) > skew || MathF.Abs(Vector3.Dot(z, x)) > skew
            || !Matrix4x4.Invert(transform, out var inverse) || !SdfMath.IsFinite(inverse))
        {
            throw new ArgumentException(
                $"SDF shape {index}: the transform must be a finite rotation, translation and uniform scale; " +
                "shear or non-uniform scale would no longer yield a distance.");
        }

        var size = shape.Size;
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || !float.IsFinite(size.Z) || !float.IsFinite(size.W))
            throw new ArgumentException($"SDF shape {index}: the size must be finite.");
        size = Vector4.Max(size, Vector4.Zero);
        float localRadius;
        switch (shape.Kind)
        {
            case SdfShapeKind.Sphere:
                size = new Vector4(size.X, 0f, 0f, 0f);
                localRadius = size.X;
                break;
            case SdfShapeKind.Box:
                size.W = MathF.Min(size.W, MathF.Min(size.X, MathF.Min(size.Y, size.Z)));
                localRadius = new Vector3(size.X, size.Y, size.Z).Length();
                break;
            case SdfShapeKind.Capsule:
                size = new Vector4(size.X, size.Y, 0f, 0f);
                localRadius = size.X + size.Y;
                break;
            case SdfShapeKind.Cylinder:
                size = new Vector4(size.X, size.Y, 0f, MathF.Min(size.W, MathF.Min(size.X, size.Y)));
                localRadius = new Vector2(size.X, size.Y).Length();
                break;
            case SdfShapeKind.Torus:
                size = new Vector4(size.X, size.Y, 0f, 0f);
                localRadius = size.X + size.Y;
                break;
            case SdfShapeKind.Plane:
                size = Vector4.Zero;
                localRadius = UnboundedRadius;
                break;
            default:
                throw new ArgumentException($"SDF shape {index}: unknown kind {shape.Kind}.");
        }
        if (shape.Operation > SdfOperation.Intersection)
            throw new ArgumentException($"SDF shape {index}: unknown operation {shape.Operation}.");

        var material = shape.Material;
        var smoothness = float.IsFinite(shape.Smoothness) ? MathF.Max(shape.Smoothness, 0f) : 0f;
        // Padded so rounding in the bound can never exceed the shape's true distance.
        var worldRadius = MathF.Min(localRadius * scale * 1.001f + 1e-4f, UnboundedRadius);
        return new SdfShapeGpu
        {
            WorldToLocal = inverse,
            Size = size,
            Params = new Vector4((float)shape.Kind, (float)shape.Operation, smoothness, scale),
            Bounds = new Vector4(transform.Translation, worldRadius),
            AlbedoRoughness = new Vector4(
                Vector3.Clamp(Finite(material.Albedo), Vector3.Zero, Vector3.One),
                float.IsFinite(material.Roughness) ? Math.Clamp(material.Roughness, MinRoughness, 1f) : 1f),
            EmissiveMetallic = new Vector4(
                Vector3.Max(Finite(material.Emissive), Vector3.Zero),
                float.IsFinite(material.Metallic) ? Math.Clamp(material.Metallic, 0f, 1f) : 0f),
        };
    }

    /// <summary>Large enough that a plane is never skipped, small enough to stay finite in the shader.</summary>
    private const float UnboundedRadius = 1e30f;

    /// <summary>Keeps the GGX lobe's denominator away from zero.</summary>
    private const float MinRoughness = 0.03f;

    private static Vector3 Finite(Vector3 value) => new(
        float.IsFinite(value.X) ? value.X : 0f, float.IsFinite(value.Y) ? value.Y : 0f, float.IsFinite(value.Z) ? value.Z : 0f);
}
